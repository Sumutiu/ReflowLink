// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Diagnostics;
using System.Threading;

namespace ReflowLink.Protocol {
    public enum TrafficKind { Tx, Rx, Info, Error }

    public sealed class TrafficEntry {
        public DateTime Time;
        public TrafficKind Kind;
        public byte[] Bytes;
        public string Text;
        /// <summary>True for background telemetry polling (can be hidden in the console).</summary>
        public bool IsPoll;
    }

    /// <summary>
    /// Thread-safe request/response client for a single PC410 controller.
    /// All bus access goes through <see cref="SyncRoot"/>; lock it to run several requests back-to-back.
    /// </summary>
    public sealed class Pc410Client(ITransport transport, int address) : IDisposable {
        private readonly object _sync = new();
        private readonly byte[] _rx = new byte[256];
        [ThreadStatic] private static bool _pollContext;

        public ITransport Transport { get; } = transport ?? throw new ArgumentNullException(nameof(transport));
        public int Address { get; set; } = address;
        public int ResponseTimeoutMs { get; set; } = 500;
        public int Retries { get; set; } = 2;
        /// <summary>Wire time per byte (ms) added to the response timeout; 10 bits per byte at the configured baud.</summary>
        public double TransmitMsPerByte { get; set; }
        public object SyncRoot => _sync;

        private int _waiting;

        /// <summary>
        /// True while a command (Stop, a write, a read the user asked for) waits for the bus. The background poll then
        /// skips its optional readings, so the command goes out sooner.
        /// </summary>
        public bool CommandWaiting => Volatile.Read(ref _waiting) > 0;

        /// <summary>Announces a command that's about to wait for <see cref="SyncRoot"/>; dispose once it has the lock.</summary>
        public IDisposable WaitingForBus() {
            Interlocked.Increment(ref _waiting);
            return new Scope(() => Interlocked.Decrement(ref _waiting));
        }

        public event Action<TrafficEntry> Traffic;

        /// <summary>Marks traffic generated on the current thread as background polling.</summary>
        public static IDisposable PollScope() {
            bool prev = _pollContext;
            _pollContext = true;
            return new Scope(() => _pollContext = prev);
        }

        private sealed class Scope(Action a) : IDisposable {
            private Action _a = a;

            public void Dispose() { _a?.Invoke(); _a = null; }
        }

        // ------------------------------------------------------------------ public API

        public string Read(string mnemonic) => Read(mnemonic, Retries);

        /// <summary>
        /// Reads a value the poll can do without (setpoint, output, step, status, IN2, A2): asked once, without retries,
        /// so a parameter that doesn't answer costs one timeout instead of one per attempt.
        /// </summary>
        public string ReadOptional(string mnemonic) => Read(mnemonic, 0);

        private string Read(string mnemonic, int retries) {
            lock (_sync) {
                Pc410Exception last = null;
                for (int attempt = 0; attempt <= retries; attempt++) {
                    try { return ReadOnce(mnemonic); } catch (Pc410NakException) {
                        Log(TrafficKind.Error, null, $"Read {mnemonic}: rejected by controller");
                        throw new Pc410NakException($"Controller rejected a read of '{mnemonic}' (parameter not supported?).");
                    } catch (Pc410Exception ex) {
                        last = ex;
                        Log(TrafficKind.Error, null, $"Read {mnemonic}: {ex.Message}" + (attempt < retries ? " — retrying" : ""));
                        if (attempt < retries) Thread.Sleep(40);
                    } catch (Exception ex) when (Pc410PortException.IsPortError(ex)) {
                        // The port itself failed: retrying won't help, and callers handle it like any controller error.
                        Log(TrafficKind.Error, null, $"Read {mnemonic}: serial port error: {ex.Message}");
                        throw new Pc410PortException(ex);
                    }
                }
                throw last;
            }
        }

        public void Write(string mnemonic, string data) {
            lock (_sync) {
                Pc410Exception last = null;
                for (int attempt = 0; attempt <= Retries; attempt++) {
                    try { WriteOnce(mnemonic, data); return; } catch (Pc410NakException) {
                        Log(TrafficKind.Error, null, $"Write {mnemonic}={data}: rejected (NAK)");
                        throw new Pc410NakException($"Controller rejected {mnemonic} = {data}.");
                    } catch (Pc410Exception ex) {
                        last = ex;
                        Log(TrafficKind.Error, null, $"Write {mnemonic}={data}: {ex.Message}" + (attempt < Retries ? " — retrying" : ""));
                        if (attempt < Retries) Thread.Sleep(40);
                    } catch (Exception ex) when (Pc410PortException.IsPortError(ex)) {
                        Log(TrafficKind.Error, null, $"Write {mnemonic}={data}: serial port error: {ex.Message}");
                        throw new Pc410PortException(ex);
                    }
                }
                throw last;
            }
        }

        public double ReadNumber(string mnemonic) => ParseNumber(mnemonic, Read(mnemonic));

        /// <summary>A number the poll can do without: asked once (see <see cref="ReadOptional"/>).</summary>
        public double ReadOptionalNumber(string mnemonic) => ParseNumber(mnemonic, ReadOptional(mnemonic));

        private static double ParseNumber(string mnemonic, string d) {
            if (!Pc410Protocol.TryParseNumber(d, out double v))
                throw new Pc410FrameException($"'{mnemonic}' returned a non-numeric value '{d}'.");
            return v;
        }

        public int ReadHexWord(string mnemonic) => ParseHexWord(mnemonic, Read(mnemonic));

        /// <summary>A status word the poll can do without: asked once (see <see cref="ReadOptional"/>).</summary>
        public int ReadOptionalHexWord(string mnemonic) => ParseHexWord(mnemonic, ReadOptional(mnemonic));

        private static int ParseHexWord(string mnemonic, string d) {
            if (Pc410Protocol.TryParseHexWord(d, out int v)) return v;
            if (Pc410Protocol.TryParseNumber(d, out double dv)) return (int)dv;
            throw new Pc410FrameException($"'{mnemonic}' returned an invalid status word '{d}'.");
        }

        public void WriteNumber(string mnemonic, double value, int maxDecimals = 2) =>
            Write(mnemonic, Pc410Protocol.FormatNumber(value, maxDecimals));

        public void WriteInteger(string mnemonic, double value) =>
            Write(mnemonic, Pc410Protocol.FormatInteger(value));

        public void WriteHexWord(string mnemonic, int value) =>
            Write(mnemonic, Pc410Protocol.FormatHexWord(value));

        // ------------------------------------------------------------------ transactions

        private string ReadOnce(string mnemonic) {
            var frame = Pc410Protocol.BuildReadFrame(Address, mnemonic);
            Transport.DiscardInput();
            Log(TrafficKind.Tx, frame, null);
            Transport.Write(frame);

            int count = 0;
            int echoToSkip = 0;
            int timeout = ResponseTimeoutMs + (int)Math.Ceiling(frame.Length * TransmitMsPerByte);
            var sw = Stopwatch.StartNew();
            while (true) {
                int remaining = timeout - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int b = Transport.ReadByte(remaining);
                if (b < 0) break;
                if (count < _rx.Length) _rx[count++] = (byte)b;

                // Half-duplex RS485 adapters may echo our own request back; skip it.
                if (count == 1 && b == Pc410Protocol.EOT) echoToSkip = frame.Length;
                if (echoToSkip > 0) { echoToSkip--; if (echoToSkip == 0) count = 0; continue; }
                if (count == 1 && b == Pc410Protocol.NAK) { Log(TrafficKind.Rx, Slice(_rx, 0, 1), null); throw new Pc410NakException("NAK"); }

                int len = Pc410Protocol.FindCompleteReadResponse(_rx, count, out int skip);
                if (len > 0) {
                    Log(TrafficKind.Rx, Slice(_rx, skip, len), null);
                    return Pc410Protocol.ParseReadResponse(_rx, skip, len, mnemonic);
                }
            }
            if (count > 0) {
                Log(TrafficKind.Rx, Slice(_rx, 0, count), null);
                throw new Pc410FrameException("Incomplete reply from controller.");
            }
            throw new Pc410TimeoutException($"No reply (address {Address:00}).");
        }

        private void WriteOnce(string mnemonic, string data) {
            var frame = Pc410Protocol.BuildWriteFrame(Address, mnemonic, data);
            Transport.DiscardInput();
            Log(TrafficKind.Tx, frame, null);
            Transport.Write(frame);

            int count = 0;
            int echoToSkip = 0;
            int timeout = ResponseTimeoutMs + (int)Math.Ceiling(frame.Length * TransmitMsPerByte);
            var sw = Stopwatch.StartNew();
            while (true) {
                int remaining = timeout - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int b = Transport.ReadByte(remaining);
                if (b < 0) break;
                if (count < _rx.Length) _rx[count++] = (byte)b;

                // Half-duplex RS485 adapters may echo our own frame back; skip it.
                if (count == 1 && b == Pc410Protocol.EOT) echoToSkip = frame.Length;
                if (echoToSkip > 0) { echoToSkip--; continue; }

                if (b == Pc410Protocol.ACK) { Log(TrafficKind.Rx, Slice(_rx, count - 1, 1), null); return; }
                if (b == Pc410Protocol.NAK) { Log(TrafficKind.Rx, Slice(_rx, count - 1, 1), null); throw new Pc410NakException("NAK"); }
            }
            if (count > 0) {
                Log(TrafficKind.Rx, Slice(_rx, 0, count), null);
                throw new Pc410FrameException("Unexpected reply to write.");
            }
            throw new Pc410TimeoutException($"No acknowledgement (address {Address:00}).");
        }

        private static byte[] Slice(byte[] src, int offset, int len) {
            var r = new byte[len];
            Buffer.BlockCopy(src, offset, r, 0, len);
            return r;
        }

        private void Log(TrafficKind kind, byte[] bytes, string text) {
            var h = Traffic;
            if (h == null) return;
            try { h(new TrafficEntry { Time = DateTime.Now, Kind = kind, Bytes = bytes, Text = text, IsPoll = _pollContext }); } catch { /* never let logging break the bus */ }
        }

        public void Dispose() => Transport.Dispose();
    }
}
