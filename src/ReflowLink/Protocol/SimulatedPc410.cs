// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using ReflowLink.Model;

namespace ReflowLink.Protocol {
    /// <summary>
    /// Byte-accurate PC410 emulator with a simple thermal model of an IR heater and board.
    /// It speaks the real protocol, so the whole stack (framing, checksums, retries) is exercised.
    /// </summary>
    public sealed class SimulatedPc410 : ITransport {
        private sealed class Segment {
            public string Ramp = "0";
            public double Level;
            /// <summary>Dwell in the controller's native unit (seconds or minutes).</summary>
            public double Dwell;
        }

        private enum Phase { Ramp, Dwell }

        private const double Ambient = 24.0;
        private const double Gain = 2.6;    // °C of equilibrium rise per % output
        private const double Tau = 45.0;    // thermal time constant, s
        private const double MaxTemp = 400;

        private readonly object _lock = new();
        private readonly Queue<byte> _out = new();
        private readonly Random _rng = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Segment[,] _patterns = new Segment[10, 8];
        private readonly Dictionary<string, string> _misc = new(StringComparer.Ordinal);
        private double _lastUpdate;

        private bool _open;
        private double _pv = Ambient, _sp, _op, _integral;
        private int _pattern;
        private ProgramState _state = ProgramState.Idle;
        private int _segIndex;
        private Phase _phase;
        private double _dwellLeft;
        private double _currentRate;

        public SimulatedPc410(int address, double speed = 1.0, ControllerUnits units = null) {
            Address = address;
            Speed = speed;
            Units = units ?? ControllerUnits.SecondsBased;
            for (int p = 0; p < 10; p++)
                for (int s = 0; s < 8; s++)
                    _patterns[p, s] = new Segment();

            // Pattern 0 ships with a small demo profile.
            Set(0, 0, 1.5, 120, 20);
            Set(0, 1, 0.8, 160, 40);
            Set(0, 2, 1.0, 205, 20);

            _misc["SL"] = "25.0";
            _misc["Hb"] = "0";
            _misc["XP"] = "8.0";
            _misc["TI"] = "120";
            _misc["TD"] = "30";
            _misc["HS"] = "230";
            _misc["LS"] = "0";
            _misc["HA"] = "250";
            _misc["LA"] = "0";
            _misc["CH"] = "2";
            _misc["XS"] = ">0000";
            _misc["Lc"] = "1";
            _sp = 25.0;
        }

        /// <summary>Stores a segment given in °C/s and seconds, converted to the controller's units.</summary>
        private void Set(int p, int s, double rampPerSecond, double level, int dwellSeconds) {
            _patterns[p, s].Ramp = Pc410Protocol.FormatNumber(Units.RampToNative(rampPerSecond), Units.RampDecimals);
            _patterns[p, s].Level = level;
            _patterns[p, s].Dwell = Units.DwellToNative(dwellSeconds);
        }

        public int Address { get; set; }
        /// <summary>Units this emulated controller is configured for.</summary>
        public ControllerUnits Units { get; }
        /// <summary>Simulation speed multiplier (1 = real time).</summary>
        public double Speed { get; set; }
        public string Name => "Simulator" + (Speed > 1 ? $" · {Speed:0}× speed" : "");
        public bool IsOpen => _open;

        public void Open() { _open = true; lock (_lock) { _lastUpdate = _clock.Elapsed.TotalSeconds; } }
        public void Close() { _open = false; }
        public void Dispose() { Close(); }

        public void DiscardInput() { lock (_lock) _out.Clear(); }

        public int ReadByte(int timeoutMs) {
            if (!_open) throw new InvalidOperationException("Port is closed.");
            lock (_lock) {
                if (_out.Count > 0) return _out.Dequeue();
            }
            Thread.Sleep(Math.Max(1, timeoutMs)); // real controller stays silent on errors
            lock (_lock) { return _out.Count > 0 ? _out.Dequeue() : -1; }
        }

        public void Write(byte[] data) {
            if (!_open) throw new InvalidOperationException("Port is closed.");
            Thread.Sleep(6 + data.Length); // transmission + controller turnaround
            lock (_lock) {
                Update();
                var reply = Handle(data);
                if (reply != null) foreach (var b in reply) _out.Enqueue(b);
            }
        }

        // ------------------------------------------------------------------ protocol

        private byte[] Handle(byte[] f) {
            if (f.Length < 8 || f[0] != Pc410Protocol.EOT) return null;
            if (f[1] != f[2] || f[3] != f[4]) return null; // malformed address: silence
            if (!char.IsDigit((char)f[1]) || !char.IsDigit((char)f[3])) return null;
            int addr = (f[1] - '0') * 10 + (f[3] - '0');
            if (addr != Address) return null;

            if (f[5] == Pc410Protocol.STX) {
                int etx = Array.IndexOf(f, Pc410Protocol.ETX, 6);
                if (etx < 8 || etx + 1 >= f.Length) return null;
                byte bcc = Pc410Protocol.Bcc(f, 6, etx - 5);
                if (bcc != f[etx + 1]) return [Pc410Protocol.NAK];
                string mn = Encoding.ASCII.GetString(f, 6, 2);
                string data = Encoding.ASCII.GetString(f, 8, etx - 8);
                return [HandleWrite(mn, data) ? Pc410Protocol.ACK : Pc410Protocol.NAK];
            }
            if (f.Length == 8 && f[7] == Pc410Protocol.ENQ) {
                string mn = Encoding.ASCII.GetString(f, 5, 2);
                string v = HandleRead(mn);
                return v == null ? null : Pc410Protocol.BuildReadResponse(mn, v);
            }
            return null;
        }

        private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        private string HandleRead(string mn) {
            switch (mn) {
                case "PV": return F1(Math.Round(_pv + (_rng.NextDouble() - 0.5) * 0.3, 1));
                case "SP": return F1(_state == ProgramState.Idle ? ParseOr(_misc["SL"], 25) : _sp);
                case "OP": return Math.Round(_op).ToString(CultureInfo.InvariantCulture);
                case "OS": return Pc410Protocol.FormatHexWord(_state == ProgramState.Running ? Pc410Device.OsRun : _state == ProgramState.Hold ? Pc410Device.OsHold : Pc410Device.OsStop);
                case "SW": return Pc410Protocol.FormatHexWord(_state == ProgramState.Idle ? 0x0000 : 0x0001);
                case "SE": return _state == ProgramState.Idle ? "0" : (_segIndex + 1).ToString(CultureInfo.InvariantCulture);
                case "ch": return _pattern.ToString(CultureInfo.InvariantCulture);
            }
            if (mn.Length == 2 && (mn[0] == 'r' || mn[0] == 'l' || mn[0] == 't') && mn[1] >= '1' && mn[1] <= '8') {
                var s = _patterns[_pattern, mn[1] - '1'];
                return mn[0] switch {
                    'r' => s.Ramp,
                    'l' => F1(s.Level),
                    _ => Pc410Protocol.FormatNumber(s.Dwell, Units.DwellDecimals),
                };
            }
            return _misc.TryGetValue(mn, out var val) ? val : null; // unknown parameter: silence
        }

        private bool HandleWrite(string mn, string data) {
            switch (mn) {
                case "PV": case "SP": case "OP": case "SE": return false; // read-only
                case "OS":
                    if (!Pc410Protocol.TryParseHexWord(data, out int w)) return false;
                    return SetOs(w);
                case "ch":
                    if (_state != ProgramState.Idle) return false;
                    if (!int.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch) || ch < 0 || ch > 9) return false;
                    _pattern = ch;
                    return true;
                case "SW":
                    return Pc410Protocol.TryParseHexWord(data, out _);
            }
            if (mn.Length == 2 && (mn[0] == 'r' || mn[0] == 'l' || mn[0] == 't') && mn[1] >= '1' && mn[1] <= '8') {
                var s = _patterns[_pattern, mn[1] - '1'];
                switch (mn[0]) {
                    case 'r':
                        if (Pc410Protocol.IsEnd(data)) { s.Ramp = Pc410Protocol.EndValue; return true; }
                        if (!Pc410Protocol.TryParseNumber(data, out double r) || r < 0 || r > 999.9) return false;
                        s.Ramp = Pc410Protocol.FormatNumber(r, Units.RampDecimals);
                        return true;
                    case 'l':
                        if (!Pc410Protocol.TryParseNumber(data, out double l) || l < 0 || l > MaxTemp) return false;
                        s.Level = l;
                        return true;
                    default:
                        if (!Pc410Protocol.TryParseNumber(data, out double t) || t < 0 || t > 9999) return false;
                        s.Dwell = Units.Dwell == DwellUnit.Seconds ? Math.Round(t) : t;
                        return true;
                }
            }
            if (_misc.ContainsKey(mn)) {
                if (!Pc410Protocol.TryParseNumber(data, out _)) return false;
                _misc[mn] = data;
                return true;
            }
            return false;
        }

        private bool SetOs(int w) {
            switch (w) {
                case Pc410Device.OsStop:
                    _state = ProgramState.Idle;
                    return true;
                case Pc410Device.OsRun:
                    if (_state == ProgramState.Idle) {
                        _segIndex = 0;
                        _phase = Phase.Ramp;
                        _sp = _pv;
                        _integral = 0;
                    }
                    _state = ProgramState.Running;
                    return true;
                case Pc410Device.OsHold:
                    if (_state == ProgramState.Idle) return false;
                    _state = ProgramState.Hold;
                    return true;
            }
            return false;
        }

        private static double ParseOr(string s, double fallback) =>
            Pc410Protocol.TryParseNumber(s, out double v) ? v : fallback;

        // ------------------------------------------------------------------ physics

        private void Update() {
            double now = _clock.Elapsed.TotalSeconds;
            double span = (now - _lastUpdate) * Speed;
            _lastUpdate = now;
            if (span <= 0) return;
            if (span > 3600) span = 3600;
            int steps = (int)Math.Ceiling(span / 0.1);
            if (steps > 5000) steps = 5000;
            double dt = span / steps;
            for (int i = 0; i < steps; i++) Step(dt);
        }

        private bool SegmentEnds(Segment s) {
            if (Pc410Protocol.IsEnd(s.Ramp)) return true;
            double r = ParseOr(s.Ramp, 0);
            return r <= 0 && s.Level <= 0 && s.Dwell <= 0;
        }

        private void Step(double dt) {
            double hb = ParseOr(_misc["Hb"], 0);
            _currentRate = 0;

            if (_state == ProgramState.Running) {
                if (_segIndex >= 8 || SegmentEnds(_patterns[_pattern, _segIndex])) {
                    _state = ProgramState.Idle; // program complete
                } else {
                    var seg = _patterns[_pattern, _segIndex];
                    bool held = hb > 0 && Math.Abs(_pv - _sp) > hb;
                    if (_phase == Phase.Ramp) {
                        double rate = Units.RampFromNative(ParseOr(seg.Ramp, 0));
                        if (rate <= 0) _sp = seg.Level;
                        else if (!held) {
                            double d = seg.Level - _sp;
                            double stepMax = rate * dt;
                            if (Math.Abs(d) <= stepMax) _sp = seg.Level;
                            else { _sp += Math.Sign(d) * stepMax; _currentRate = Math.Sign(d) * rate; }
                        }
                        if (Math.Abs(_sp - seg.Level) < 1e-9) { _phase = Phase.Dwell; _dwellLeft = Units.Dwell == DwellUnit.Minutes ? seg.Dwell * 60 : seg.Dwell; }
                    } else {
                        if (!held) _dwellLeft -= dt;
                        if (_dwellLeft <= 0) { _segIndex++; _phase = Phase.Ramp; }
                    }
                }
            }

            // Controller: feed-forward + PI.
            if (_state is ProgramState.Running or ProgramState.Hold) {
                double err = _sp - _pv;
                _integral = Clamp(_integral + err * dt * 0.05, -25, 25);
                double ff = (_sp - Ambient) / Gain + Math.Max(0, _currentRate) * Tau / Gain;
                _op = Clamp(ff + 6.0 * err + _integral, 0, 100);
            } else {
                _op = 0;
                _integral = 0;
            }

            // Plant: first-order lag towards the equilibrium temperature for the current output.
            double teq = Ambient + Gain * _op;
            _pv += (teq - _pv) * dt / Tau;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
