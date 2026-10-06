// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ReflowLink.Model;

namespace ReflowLink.Protocol {
    public enum ProgramState { Unknown, Idle, Running, Hold }

    public enum UnusedStepMode {
        /// <summary>Write 0 / 0 / 0 to every unused segment (behaviour confirmed on ACHI stations).</summary>
        Zeros,
        /// <summary>Write ramp = END to the first unused segment, zeros after it.</summary>
        EndMarker,
    }

    public sealed class Telemetry {
        public DateTime Time;
        public double? Pv;
        public double? Sp;
        public double? Op;
        public int? Os;
        public int? Segment;
        public int? StatusWord;
        public int? Pattern;
        public ProgramState State;
    }

    public struct OperationProgress(int done, int total, string text) {
        public int Done = done;
        public int Total = total;
        public string Text = text;
    }

    public sealed class UploadResult {
        public List<string> Warnings = [];
    }

    /// <summary>High-level PC410 operations (telemetry, profiles, program control).</summary>
    public sealed class Pc410Device(Pc410Client client) {
        public const string Pv = "PV", Sp = "SP", Op = "OP", Os = "OS", Sw = "SW", Se = "SE", Pattern = "ch", Holdback = "Hb", Sl = "SL";

        public const int OsStop = 0x0000, OsRun = 0x0002, OsHold = 0x0003;

        private bool _seSupported = true, _swSupported = true;
        private int _seMisses, _swMisses;
        private const int UnsupportedAfter = 3;
        private int _cycle;

        public Pc410Client Client { get; } = client;

        /// <summary>Units the controller uses for ramp (r1..r8) and dwell (t1..t8) values.</summary>
        public ControllerUnits Units { get; set; } = ControllerUnits.SecondsBased;

        public static ProgramState DecodeOs(int word) {
            if (word == 0) return ProgramState.Idle;
            if ((word & 0x0004) != 0 || (word & 0x0003) == 0x0003) return ProgramState.Hold;
            if ((word & 0x0002) != 0) return ProgramState.Running;
            return ProgramState.Unknown;
        }

        public static string[] SegmentMnemonics(int n) {
            if (n is < 1 or > Pc410Protocol.SegmentCount) throw new ArgumentOutOfRangeException(nameof(n));
            string s = n.ToString(CultureInfo.InvariantCulture);
            return ["r" + s, "l" + s, "t" + s];
        }

        /// <summary>Quick check used when connecting: reads PV once.</summary>
        public double Probe() => Client.ReadNumber(Pv);

        public Telemetry ReadTelemetry(bool includePattern = false) {
            lock (Client.SyncRoot) {
                var t = new Telemetry {
                    Time = DateTime.Now,
                    Pv = Client.ReadNumber(Pv),
                    Sp = TryNumber(Sp),
                    Op = TryNumber(Op)
                };
                try { t.Os = Client.ReadHexWord(Os); t.State = DecodeOs(t.Os.Value); } catch (Pc410Exception) { t.State = ProgramState.Unknown; }

                // Optional parameters: give up on them only after repeated failures
                // in cycles where PV itself answered (so a cable glitch does not disable them).
                if (_seSupported) {
                    try { t.Segment = (int)Client.ReadNumber(Se); _seMisses = 0; } catch (Pc410NakException) { _seSupported = false; } catch (Pc410Exception) { if (++_seMisses >= UnsupportedAfter) _seSupported = false; }
                }
                if (_swSupported && (_cycle % 5 == 0)) {
                    try { t.StatusWord = Client.ReadHexWord(Sw); _swMisses = 0; } catch (Pc410NakException) { _swSupported = false; } catch (Pc410Exception) { if (++_swMisses >= UnsupportedAfter) _swSupported = false; }
                }
                if (includePattern) {
                    try { t.Pattern = ReadPattern(); } catch (Pc410Exception) { }
                }
                _cycle++;
                return t;
            }
        }

        private double? TryNumber(string m) {
            try { return Client.ReadNumber(m); } catch (Pc410Exception) { return null; }
        }

        public ProgramState ReadState() {
            return DecodeOs(Client.ReadHexWord(Os));
        }

        public int ReadPattern() {
            return (int)Math.Round(Client.ReadNumber(Pattern));
        }

        public void SelectPattern(int slot) {
            if (slot is < Pc410Protocol.PatternMin or > Pc410Protocol.PatternMax)
                throw new ArgumentOutOfRangeException(nameof(slot), "Pattern must be 0..9.");
            lock (Client.SyncRoot) {
                EnsureIdle("change the active pattern");
                Client.WriteInteger(Pattern, slot);
                int actual = ReadPattern();
                if (actual != slot)
                    throw new Pc410Exception($"Controller reports pattern {actual} after selecting {slot}.");
            }
        }

        private void EnsureIdle(string action) {
            ProgramState st;
            try { st = ReadState(); } catch (Pc410Exception) { return; } // OS not readable: let the controller decide.
            if (st is ProgramState.Running or ProgramState.Hold)
                throw new Pc410Exception($"Stop the running program before trying to {action}.");
        }

        public ReflowProfile DownloadProfile(int slot, IProgress<OperationProgress> progress, CancellationToken ct) {
            lock (Client.SyncRoot) {
                int total = 2 + Pc410Protocol.SegmentCount;
                progress?.Report(new OperationProgress(0, total, $"Selecting pattern {slot}…"));
                SelectPattern(slot);

                var p = new ReflowProfile { Name = "" };
                progress?.Report(new OperationProgress(1, total, "Reading holdback…"));
                try { p.Holdback = Client.ReadNumber(Holdback); } catch (Pc410Exception) { p.Holdback = null; }

                bool ended = false;
                for (int n = 1; n <= Pc410Protocol.SegmentCount; n++) {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(1 + n, total, $"Reading step {n}…"));
                    if (ended) continue;
                    var m = SegmentMnemonics(n);
                    string rampRaw = Client.Read(m[0]);
                    if (Pc410Protocol.IsEnd(rampRaw)) { ended = true; continue; }
                    double target = Client.ReadNumber(m[1]);
                    double hold = Client.ReadNumber(m[2]);
                    if (!Pc410Protocol.TryParseNumber(rampRaw, out double ramp))
                        throw new Pc410FrameException($"Step {n}: invalid ramp value '{rampRaw}'.");
                    if (ramp <= 0 && target <= 0 && hold <= 0) { ended = true; continue; }
                    p.Steps.Add(new ProfileStep(Math.Round(Units.RampFromNative(ramp), 4), target, Units.DwellFromNative(hold)));
                }
                progress?.Report(new OperationProgress(total, total, "Done"));
                return p;
            }
        }

        public UploadResult UploadProfile(ReflowProfile profile, int slot, UnusedStepMode unusedMode, bool verify,
            IProgress<OperationProgress> progress, CancellationToken ct) {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.Steps.Count is < 1 or > Pc410Protocol.SegmentCount)
                throw new ArgumentException("Profile must have 1 to 8 steps.");

            var result = new UploadResult();
            lock (Client.SyncRoot) {
                int total = 2 + Pc410Protocol.SegmentCount * (verify ? 2 : 1);
                int done = 0;
                progress?.Report(new OperationProgress(done, total, $"Selecting pattern {slot}…"));
                SelectPattern(slot);
                done++;

                progress?.Report(new OperationProgress(done, total, "Writing holdback…"));
                if (profile.Holdback.HasValue) {
                    try { Client.WriteNumber(Holdback, profile.Holdback.Value, 1); } catch (Pc410Exception ex) { result.Warnings.Add("Holdback: " + ex.Message); }
                }
                done++;

                // Planned (mnemonic, data) for each segment so we can verify afterwards.
                var planned = new List<string[]>();
                for (int n = 1; n <= Pc410Protocol.SegmentCount; n++) {
                    var m = SegmentMnemonics(n);
                    if (n <= profile.Steps.Count) {
                        var s = profile.Steps[n - 1];
                        planned.Add([m[0], Pc410Protocol.FormatNumber(Units.RampToNative(s.RampRate), Units.RampDecimals)]);
                        planned.Add([m[1], Pc410Protocol.FormatNumber(s.Target, 1)]);
                        planned.Add([m[2], Pc410Protocol.FormatNumber(Units.DwellToNative(s.HoldSeconds), Units.DwellDecimals)]);
                    } else {
                        bool first = n == profile.Steps.Count + 1;
                        planned.Add([m[0], unusedMode == UnusedStepMode.EndMarker && first ? Pc410Protocol.EndValue : "0"]);
                        planned.Add([m[1], "0"]);
                        planned.Add([m[2], "0"]);
                    }
                }

                for (int n = 1; n <= Pc410Protocol.SegmentCount; n++) {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(done, total, $"Writing step {n}…"));
                    for (int k = 0; k < 3; k++) {
                        var item = planned[(n - 1) * 3 + k];
                        Client.Write(item[0], item[1]);
                    }
                    done++;
                }

                if (verify) {
                    for (int n = 1; n <= Pc410Protocol.SegmentCount; n++) {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new OperationProgress(done, total, $"Verifying step {n}…"));
                        for (int k = 0; k < 3; k++) {
                            var item = planned[(n - 1) * 3 + k];
                            string back = Client.Read(item[0]);
                            if (!ValuesMatch(item[1], back))
                                result.Warnings.Add($"Step {n} {item[0]}: wrote {item[1]}, controller reports {back.Trim()}.");
                        }
                        done++;
                    }
                }
                progress?.Report(new OperationProgress(total, total, "Done"));
            }
            return result;
        }

        public static bool ValuesMatch(string written, string readBack) {
            if (Pc410Protocol.IsEnd(written) || Pc410Protocol.IsEnd(readBack))
                return Pc410Protocol.IsEnd(written) && Pc410Protocol.IsEnd(readBack);
            if (Pc410Protocol.TryParseNumber(written, out double a) && Pc410Protocol.TryParseNumber(readBack, out double b))
                return Math.Abs(a - b) <= 0.051;
            return string.Equals(written.Trim(), readBack.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public void Start(int? slot) {
            lock (Client.SyncRoot) {
                if (slot.HasValue) {
                    int current = -1;
                    try { current = ReadPattern(); } catch (Pc410Exception) { }
                    if (current != slot.Value) SelectPattern(slot.Value);
                }
                Client.WriteHexWord(Os, OsRun);
            }
        }

        public void Stop() => Client.WriteHexWord(Os, OsStop);
        public void Hold() => Client.WriteHexWord(Os, OsHold);
        public void Resume() => Client.WriteHexWord(Os, OsRun);
    }
}
