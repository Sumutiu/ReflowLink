// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using ReflowLink.Model;
using ReflowLink.Protocol;

namespace ReflowLink.Services {
    public enum LinkStatus { Disconnected, Connecting, Connected, NoResponse }

    /// <summary>
    /// Owns the connection and the background telemetry poller.
    /// Events are raised on the UI thread (the synchronization context that created the service).
    /// </summary>
    public sealed class DeviceService : IDisposable {
        private readonly SynchronizationContext _ui;
        private Pc410Client _client;
        private Pc410Device _device;
        private Thread _pollThread;
        private PollSession _session;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>Stop flag owned by one connection, so a late poller can never mix with a new session.</summary>
        private sealed class PollSession {
            public volatile bool Running = true;
            public Pc410Device Device;
        }
        private ProgramState _lastState = ProgramState.Unknown;

        public DeviceService() {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        public LinkStatus State { get; private set; } = LinkStatus.Disconnected;
        public string LinkName { get; private set; } = "";
        public bool IsSimulator { get; private set; }
        public int Address { get; private set; }
        public Telemetry Last { get; private set; }
        public int? ActivePattern { get; private set; }
        public string LastError { get; private set; } = "";
        public int PollIntervalMs { get; set; } = 500;
        /// <summary>Process-time multiplier: the simulator can run faster than real time.</summary>
        public double TimeScale { get; private set; } = 1;

        public bool IsConnected => State is LinkStatus.Connected or LinkStatus.NoResponse;

        public event EventHandler StateChanged;
        public event Action<Telemetry> TelemetryReceived;
        public event Action<TrafficEntry> Traffic;
        public event Action<ProgramState, ProgramState> ProgramStateChanged;

        public async Task ConnectAsync(AppSettings s) {
            if (IsConnected || State == LinkStatus.Connecting) return;
            SetState(LinkStatus.Connecting);
            try {
                ITransport transport;
                if (s.UseSimulator) {
                    transport = new SimulatedPc410(s.Address, s.SimulatorSpeed, s.Units);
                } else {
                    if (string.IsNullOrWhiteSpace(s.PortName)) throw new InvalidOperationException("Select a serial port first.");
                    transport = new SerialTransport(new SerialSettings {
                        PortName = s.PortName,
                        BaudRate = s.BaudRate,
                        DataBits = 7,
                        Parity = Parity.Even,
                        StopBits = StopBits.One,
                        DtrEnable = s.DtrEnable,
                        RtsEnable = s.RtsEnable,
                    });
                }

                var client = new Pc410Client(transport, s.Address) {
                    ResponseTimeoutMs = s.ResponseTimeoutMs,
                    Retries = s.Retries,
                    // Writes are buffered: allow for the time the request itself takes on the wire (10 bits per byte).
                    TransmitMsPerByte = s.UseSimulator ? 0 : 10000.0 / Math.Max(300, s.BaudRate),
                };
                client.Traffic += OnTraffic;
                var device = new Pc410Device(client) { Units = s.Units };

                int? pattern = null;
                try {
                    await Task.Run(() => {
                        transport.Open();
                        device.Probe();
                        try { pattern = device.ReadPattern(); } catch (Pc410Exception) { }
                    }).ConfigureAwait(true);
                } catch (Exception) {
                    client.Traffic -= OnTraffic;
                    client.Dispose();
                    throw;
                }

                _client = client;
                _device = device;
                LinkName = transport.Name;
                IsSimulator = s.UseSimulator;
                TimeScale = s.UseSimulator ? Math.Max(1, s.SimulatorSpeed) : 1;
                Address = s.Address;
                ActivePattern = pattern;
                PollIntervalMs = s.PollIntervalMs;
                _lastState = ProgramState.Unknown;
                LastError = "";
                SetState(LinkStatus.Connected);
                StartPolling(device);
            } catch (Exception ex) {
                LastError = Describe(ex);
                SetState(LinkStatus.Disconnected);
                throw new InvalidOperationException(LastError, ex);
            }
        }

        public static string Describe(Exception ex) {
            if (ex is Pc410TimeoutException)
                return "The controller did not answer. Check the cable, COM port, baud rate (the PC410 uses 7E1) and the device address.";
            if (ex is UnauthorizedAccessException)
                return "The serial port is in use by another program (close PSoft or any terminal using it).";
            if (ex is IOException) return "Serial port error: " + ex.Message;
            return ex.Message;
        }

        public void Disconnect() {
            var c = _client;
            _client = null;
            _device = null;
            StopPolling(c);
            if (c != null) {
                c.Traffic -= OnTraffic;
                try { c.Dispose(); } catch { }
            }
            Last = null;
            ActivePattern = null;
            _lastState = ProgramState.Unknown;
            SetState(LinkStatus.Disconnected);
        }

        /// <summary>Runs an exclusive operation on the bus (polling pauses between requests while it runs).</summary>
        public Task<T> RunAsync<T>(Func<Pc410Device, T> op) {
            var d = _device ?? throw new InvalidOperationException("Not connected.");
            return Task.Run(() => {
                lock (d.Client.SyncRoot) {
                    var r = op(d);
                    return r;
                }
            });
        }

        public Task RunAsync(Action<Pc410Device> op) => RunAsync<bool>(d => { op(d); return true; });

        /// <summary>Applies new controller units (station changed while connected).</summary>
        public void SetUnits(ControllerUnits units) {
            var d = _device;
            if (d == null) return;
            lock (d.Client.SyncRoot) d.Units = units;
        }

        public void PollNow() {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        public void NotePattern(int pattern) {
            ActivePattern = pattern;
            Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
        }

        // ------------------------------------------------------------------ polling

        private void StartPolling(Pc410Device device) {
            var session = new PollSession { Device = device };
            _session = session;
            _pollThread = new Thread(() => PollLoop(session)) { IsBackground = true, Name = "PC410 poller" };
            _pollThread.Start();
        }

        private void StopPolling(Pc410Client client) {
            var s = _session;
            _session = null;
            s?.Running = false;
            try { _wake.Set(); } catch (ObjectDisposedException) { }
            // Closing the port first makes any blocking read return immediately.
            try { client?.Transport.Close(); } catch { }
            var t = _pollThread;
            _pollThread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(10000);
        }

        private void PollLoop(PollSession session) {
            int failures = 0;
            int cycle = 0;
            var sw = new Stopwatch();
            var dev = session.Device;
            while (session.Running) {
                sw.Restart();
                try {
                    Telemetry t;
                    using (Pc410Client.PollScope()) {
                        bool wantPattern = cycle % 10 == 0;
                        t = dev.ReadTelemetry(wantPattern);
                        if (!wantPattern && t.State == ProgramState.Running && _lastState != ProgramState.Running && _lastState != ProgramState.Hold) {
                            try { t.Pattern = dev.ReadPattern(); } catch (Pc410Exception) { }
                        }
                    }
                    failures = 0;
                    cycle++;
                    if (session.Running) Post(() => { if (session.Running) Deliver(t); });
                } catch (Exception ex) {
                    if (!session.Running) break;
                    failures++;
                    if (failures >= 2) {
                        string msg = Describe(ex);
                        Post(() => { if (!session.Running) return; LastError = msg; SetState(LinkStatus.NoResponse); });
                    }
                }
                if (!session.Running) break;
                int wait = PollIntervalMs - (int)sw.ElapsedMilliseconds;
                try { _wake.WaitOne(Math.Max(failures > 0 ? 400 : 30, wait)); } catch (ObjectDisposedException) { break; }
            }
        }

        private void Deliver(Telemetry t) {
            Last = t;
            if (t.Pattern.HasValue) ActivePattern = t.Pattern;
            if (State != LinkStatus.Connected) { LastError = ""; SetState(LinkStatus.Connected); }
            TelemetryReceived?.Invoke(t);
            if (t.State != ProgramState.Unknown && t.State != _lastState) {
                var prev = _lastState;
                _lastState = t.State;
                ProgramStateChanged?.Invoke(prev, t.State);
            }
        }

        private void OnTraffic(TrafficEntry e) => Post(() => Traffic?.Invoke(e));

        private void SetState(LinkStatus s) {
            State = s;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Post(Action a) => _ui.Post(_ => a(), null);

        public void Dispose() {
            Disconnect();
            // _wake is intentionally not disposed: a poller that outlived Join could still touch it.
        }
    }
}
