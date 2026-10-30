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
        /// <summary>Setpoint limits (LS / HS) read from the controller when connecting; unknown if not reported.</summary>
        public SetpointLimits ControllerLimits { get; private set; }
        public string LastError { get; private set; } = "";
        public int PollIntervalMs { get; set; } = 500;

        private volatile bool _readIn2, _readAlarm2;

        /// <summary>Also poll the second input IN2 (P2): the bed with a single PC410, or an informative reading.</summary>
        public bool ReadIn2 { get => _readIn2; set => _readIn2 = value; }

        /// <summary>Also poll alarm 2 (A2), which switches OUT2 with a single PC410.</summary>
        public bool ReadAlarm2 { get => _readAlarm2; set => _readAlarm2 = value; }

        /// <summary>Process-time multiplier: the simulator can run faster than real time.</summary>
        public double TimeScale { get; private set; } = 1;

        public bool IsConnected => State is LinkStatus.Connected or LinkStatus.NoResponse;

        public event EventHandler StateChanged;
        public event Action<Telemetry> TelemetryReceived;
        public event Action<TrafficEntry> Traffic;
        public event Action<ProgramState, ProgramState> ProgramStateChanged;

        public async Task ConnectAsync(ConnectionOptions s) {
            if (IsConnected || State == LinkStatus.Connecting) return;
            SetState(LinkStatus.Connecting);
            try {
                ITransport transport;
                if (s.UseSimulator) {
                    transport = new SimulatedPc410(s.Address, s.SimulatorSpeed, s.Units, s.SimulatorLimit);
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
                SetpointLimits limits = default;
                try {
                    await Task.Run(() => {
                        transport.Open();
                        device.Probe();
                        try { pattern = device.ReadPattern(); } catch (Pc410Exception) { }
                        limits = device.ReadSetpointLimits();
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
                ControllerLimits = limits;
                // Keep the limits current: every profile write and start check reads them again.
                device.LimitsRead += l => Post(() => {
                    if (_device != device) return;
                    ControllerLimits = l;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                });
                PollIntervalMs = s.PollIntervalMs;
                _lastState = ProgramState.Unknown;
                _lastFull = null;
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
            if (ex is Pc410PortException) return ex.Message + " Check the USB adapter, then disconnect and connect again.";
            if (ex is Pc410TimeoutException)
                return "The controller did not answer. Check the cable, COM port, baud rate (the PC410 uses 7E1) and the device address.";
            if (ex is UnauthorizedAccessException)
                return "The serial port is in use by another program (close any other station software or terminal using it).";
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
            ControllerLimits = default;
            _lastState = ProgramState.Unknown;
            _lastFull = null;
            SetState(LinkStatus.Disconnected);
        }

        /// <summary>Runs an exclusive operation on the bus (polling pauses between requests while it runs).</summary>
        public Task<T> RunAsync<T>(Func<Pc410Device, T> op) {
            var d = _device ?? throw new InvalidOperationException("Not connected.");
            return Task.Run(() => {
                // Tells the poll a command is waiting, so it skips its remaining readings and frees the bus sooner.
                var waiting = d.Client.WaitingForBus();
                lock (d.Client.SyncRoot) {
                    waiting.Dispose();
                    return op(d);
                }
            });
        }

        public Task RunAsync(Action<Pc410Device> op) => RunAsync<bool>(d => { op(d); return true; });

        /// <summary>
        /// Runs an operation on two controllers with both locked on the same thread (the first one first). Monitor locks
        /// belong to a thread, so nesting two <see cref="RunAsync{T}"/> calls could deadlock.
        /// </summary>
        public static Task<T> RunBothAsync<T>(DeviceService first, DeviceService second, Func<Pc410Device, Pc410Device, T> op) {
            var a = first._device ?? throw new InvalidOperationException("Not connected.");
            var b = second?._device;
            if (second != null && b == null) throw new InvalidOperationException("Not connected.");
            return Task.Run(() => {
                var waitingA = a.Client.WaitingForBus();
                var waitingB = b?.Client.WaitingForBus();
                try {
                    lock (a.Client.SyncRoot) {
                        waitingA.Dispose();
                        if (b == null) return op(a, null);
                        lock (b.Client.SyncRoot) {
                            waitingB.Dispose();
                            return op(a, b);
                        }
                    }
                } finally {
                    waitingA.Dispose();
                    waitingB?.Dispose();
                }
            });
        }

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
                        t = dev.ReadTelemetry(wantPattern, _readIn2, _readAlarm2);
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

        private Telemetry _lastFull;

        private void Deliver(Telemetry t) {
            // A poll cut short for a command has PV only: show the rest of the last complete poll (up to 3 s old) rather
            // than gaps. IN2 and A2 aren't copied: the bed control judges those by their own age.
            if (t.Partial && _lastFull != null && (t.Time - _lastFull.Time).TotalSeconds <= 3) {
                t.Sp ??= _lastFull.Sp;
                t.Op ??= _lastFull.Op;
                t.StatusWord ??= _lastFull.StatusWord;
                t.Segment ??= _lastFull.Segment;
                if (t.State == ProgramState.Unknown) {
                    t.State = _lastFull.State;
                    t.Os ??= _lastFull.Os;
                }
            } else if (!t.Partial) {
                _lastFull = t;
            }
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
