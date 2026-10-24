// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>Setpoint limits configured in the controller (LS / HS). Null when the controller doesn't report them.</summary>
    public readonly struct SetpointLimits(double? low, double? high) {
        public double? Low { get; } = low;
        public double? High { get; } = high;
        public bool Known => Low.HasValue || High.HasValue;
    }

    /// <summary>
    /// A profile that the controller cannot run as written: targets outside its setpoint limits,
    /// values it refused (NAK) or values it changed (clamped) while being written.
    /// </summary>
    public sealed class ProfileRejectedException(int slot, IReadOnlyList<string> issues, bool patternChanged, bool patternCleared,
        bool interrupted = false)
        : Pc410Exception(BuildMessage(slot, issues, patternChanged, patternCleared, interrupted)) {
        public int Slot { get; } = slot;
        public IReadOnlyList<string> Issues { get; } = issues;
        /// <summary>True when part of the profile had already been written to the pattern.</summary>
        public bool PatternChanged { get; } = patternChanged;
        /// <summary>True when the partly written pattern was emptied so it can't run.</summary>
        public bool PatternCleared { get; } = patternCleared;
        /// <summary>True when the write stopped because of a communication error rather than a rejected value.</summary>
        public bool Interrupted { get; } = interrupted;

        private static string BuildMessage(int slot, IReadOnlyList<string> issues, bool changed, bool cleared, bool interrupted) {
            string list = string.Join("\n", issues.Select(i => "• " + i));
            if (!changed) return $"Pattern {slot} was not changed:\n\n{list}";
            string what = interrupted ? "Writing the profile was interrupted" : "The controller rejected part of the profile";
            return cleared
                ? $"{what}, so pattern {slot} was cleared to stop it running with partial settings:\n\n{list}"
                : $"{what} and pattern {slot} could not be cleared. ReflowLink won't start it until a valid profile is written:\n\n{list}";
        }
    }

    /// <summary>The controller refused a holdback value, or stored a different one.</summary>
    public sealed class HoldbackRejectedException(string message) : Pc410Exception(message) { }

    /// <summary>The controller refused a control setting, or stored a different value.</summary>
    public sealed class SettingRejectedException(string message) : Pc410Exception(message) { }

    /// <summary>High-level PC410 operations (telemetry, profiles, program control).</summary>
    public sealed class Pc410Device(Pc410Client client) {
        public const string Pv = "PV", Sp = "SP", Op = "OP", Os = "OS", Sw = "SW", Se = "SE", Pattern = "ch", Holdback = "Hb", Sl = "SL", Hs = "HS", Ls = "LS";

        public const int OsStop = 0x0000, OsRun = 0x0002, OsHold = 0x0003;

        private bool _seSupported = true, _swSupported = true;
        // null = not tried yet, false = the controller doesn't report it, true = it answered before.
        private bool? _hsSupported, _lsSupported, _xsSupported;
        private int _seMisses, _swMisses;
        private const int UnsupportedAfter = 3;
        private int _cycle;

        public Pc410Client Client { get; } = client;

        /// <summary>Raised (on the calling thread) whenever the setpoint limits have been read from the controller.</summary>
        public event Action<SetpointLimits> LimitsRead;

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

        /// <summary>Reads the holdback band (°C). The PC410 has one holdback shared by all patterns.</summary>
        public double ReadHoldback() => Client.ReadNumber(Holdback);

        /// <summary>
        /// Writes the holdback band (°C) and reads it back. Throws if the controller refuses the value or stores a
        /// different one. Returns the value the controller holds. Applies to every pattern.
        /// </summary>
        public double WriteHoldback(double value) {
            if (value is < ReflowProfile.HoldbackMin or > ReflowProfile.HoldbackMax)
                throw new ArgumentOutOfRangeException(nameof(value), ReflowProfile.HoldbackRangeMessage);
            lock (Client.SyncRoot) {
                try {
                    Client.WriteNumber(Holdback, value, 1);
                } catch (Pc410NakException) {
                    throw new HoldbackRejectedException($"The controller refused holdback {value:0.#} °C.");
                }
                double actual = ReadHoldback();
                if (Math.Abs(actual - value) > 0.05)
                    throw new HoldbackRejectedException($"The controller stored holdback {actual:0.#} °C instead of {value:0.#} °C.");
                return actual;
            }
        }

        // ------------------------------------------------------------------ control tuning

        /// <summary>Tuning parameters this controller doesn't answer (found out once per connection).</summary>
        private readonly HashSet<string> _unsupported = [];

        /// <summary>
        /// Reads every Control Tuning setting. Settings the controller doesn't report are left null. Each parameter is
        /// read on its own, so a Stop command or the telemetry poll can get in between.
        /// </summary>
        public TuningSnapshot ReadTuning(IProgress<OperationProgress> progress, CancellationToken ct) {
            var snap = new TuningSnapshot { FromController = true, ReadAt = DateTime.Now };
            int total = TuningCatalog.All.Length + 1, done = 0;
            foreach (var p in TuningCatalog.All) {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(done++, total, $"Reading {p.Name}…"));
                snap.Values[p.Mnemonic] = TryReadSetting(p.Mnemonic);
            }
            progress?.Report(new OperationProgress(done, total, "Reading auto-tune…"));
            try { snap.AutoTune = ReadAutoTune(); } catch (Pc410Exception) { snap.AutoTune = null; }
            if (snap.Values.Values.All(v => v == null) && snap.AutoTune == null)
                throw new Pc410Exception("The controller didn't report any control settings.");
            return snap;
        }

        /// <summary>
        /// Reads one setting. A parameter that gets no answer while PV still answers is remembered as unsupported, so it
        /// doesn't cost a timeout every time. Throws when the controller doesn't answer at all.
        /// </summary>
        private double? TryReadSetting(string mnemonic) {
            if (_unsupported.Contains(mnemonic)) return null;
            try {
                return Client.ReadNumber(mnemonic);
            } catch (Pc410NakException) {
                _unsupported.Add(mnemonic);
                return null;
            } catch (Pc410Exception) {
                if (!LinkAlive()) throw;
                _unsupported.Add(mnemonic);
                return null;
            }
        }

        private bool LinkAlive() {
            try { Probe(); return true; } catch (Pc410Exception) { return false; }
        }

        /// <summary>
        /// Writes one control setting and reads it back. Refused while a program or auto-tune runs. Throws
        /// <see cref="SettingRejectedException"/> if the controller refuses the value (NAK) or stores a different one;
        /// in the second case the controller's value has changed.
        /// </summary>
        public double WriteSetting(TuningParameter p, double value) {
            if (p == null) throw new ArgumentNullException(nameof(p));
            value = p.Round(value);
            if (!p.InRange(value)) throw new ArgumentOutOfRangeException(nameof(value), p.RangeText);
            lock (Client.SyncRoot) {
                EnsureIdle("change control settings");
                if (ReadAutoTune() == true) throw new Pc410Exception("Auto-tune is running. Wait for it to finish before changing control settings.");
                try {
                    Client.WriteNumber(p.Mnemonic, value, p.Decimals);
                } catch (Pc410NakException) {
                    throw new SettingRejectedException($"The controller refused {p.Name} = {p.Format(value)}.");
                }
                double actual = Client.ReadNumber(p.Mnemonic);
                _unsupported.Remove(p.Mnemonic);
                if (!p.Same(actual, value))
                    throw new SettingRejectedException($"The controller stored {p.Name} as {p.Format(actual)} instead of {p.Format(value)}.");
                return actual;
            }
        }

        /// <summary>
        /// True while the controller's auto-tune runs; null when the controller doesn't have it. Once the controller has
        /// answered, a failed read throws instead of switching the check off.
        /// </summary>
        public bool? ReadAutoTune() {
            if (_xsSupported == false) return null;
            try {
                int word = Client.ReadHexWord(TuningCatalog.AutoTune);
                _xsSupported = true;
                return (word & 0x0001) != 0;
            } catch (Pc410Exception) when (_xsSupported == null) {
                // First attempt: no answer while PV answers means this controller has no auto-tune parameter.
                if (!LinkAlive()) throw;
                _xsSupported = false;
                return null;
            }
        }

        /// <summary>Starts or stops the controller's auto-tune (bit 0 of XS) and checks that it took effect.</summary>
        public void SetAutoTune(bool on) {
            lock (Client.SyncRoot) {
                if (on) EnsureIdle("start auto-tune");
                int word = 0;
                try { word = Client.ReadHexWord(TuningCatalog.AutoTune); } catch (Pc410Exception) { }
                word = on ? word | 0x0001 : word & ~0x0001;
                try {
                    Client.WriteHexWord(TuningCatalog.AutoTune, word);
                } catch (Pc410NakException) {
                    throw new SettingRejectedException(on ? "The controller refused to start auto-tune." : "The controller refused to stop auto-tune.");
                }
                bool running = (Client.ReadHexWord(TuningCatalog.AutoTune) & 0x0001) != 0;
                _xsSupported = true;
                if (running != on)
                    throw new SettingRejectedException(on ? "The controller didn't start auto-tune." : "The controller reports that auto-tune is still running.");
            }
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

        public void EnsureIdle(string action) {
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

        /// <summary>
        /// Reads the controller's setpoint limits (LS / HS). A parameter that doesn't answer the first time it is
        /// asked for is treated as not supported. Once it has answered, a later failure is a communication error
        /// and is thrown, so a glitch can never switch the limit checks off.
        /// </summary>
        public SetpointLimits ReadSetpointLimits() {
            lock (Client.SyncRoot) {
                double? high = ReadLimit(Hs, ref _hsSupported);
                double? low = ReadLimit(Ls, ref _lsSupported);
                var limits = new SetpointLimits(low, high);
                LimitsRead?.Invoke(limits);
                return limits;
            }
        }

        private double? ReadLimit(string mnemonic, ref bool? supported) {
            if (supported == false) return null;
            try {
                double value = Client.ReadNumber(mnemonic);
                supported = true;
                return value;
            } catch (Pc410Exception) when (supported == null) {
                supported = false;
                return null;
            }
        }

        /// <summary>Lists the steps whose targets are outside the given limits.</summary>
        public static List<string> CheckTargets(ReflowProfile profile, SetpointLimits limits, double? softwareLimit = null) {
            var issues = new List<string>();
            for (int i = 0; i < profile.Steps.Count; i++) {
                double t = profile.Steps[i].Target;
                if (softwareLimit is double max && t > max + 0.05)
                    issues.Add($"Step {i + 1}: target {t:0.#} °C is above ReflowLink's limit of {max:0.#} °C.");
                if (limits.High is double hs && t > hs + 0.05)
                    issues.Add($"Step {i + 1}: target {t:0.#} °C is above the controller's setpoint limit (HS = {hs:0.#} °C).");
                if (limits.Low is double ls && t < ls - 0.05)
                    issues.Add($"Step {i + 1}: target {t:0.#} °C is below the controller's setpoint limit (LS = {ls:0.#} °C).");
            }
            return issues;
        }

        /// <summary>
        /// Reads a pattern and checks it can run: targets within ReflowLink's limit and the controller's setpoint limits.
        /// Throws <see cref="ProfileRejectedException"/> otherwise. Used before starting a program.
        /// </summary>
        public ReflowProfile CheckPattern(int slot, double softwareLimit, IProgress<OperationProgress> progress, CancellationToken ct) {
            lock (Client.SyncRoot) {
                var profile = DownloadProfile(slot, progress, ct);
                if (profile.Steps.Count == 0)
                    throw new ProfileRejectedException(slot, ["The pattern is empty."], false, false);
                var issues = CheckTargets(profile, ReadSetpointLimits(), softwareLimit);
                if (issues.Count > 0) throw new ProfileRejectedException(slot, issues, false, false);
                return profile;
            }
        }

        /// <summary>
        /// Writes a profile to a pattern. Targets outside the controller's setpoint limits are refused before anything
        /// is written. If the controller rejects (NAK) or changes (clamps) a value during the write, writing stops,
        /// the pattern is cleared and a <see cref="ProfileRejectedException"/> is thrown.
        /// </summary>
        public UploadResult UploadProfile(ReflowProfile profile, int slot, UnusedStepMode unusedMode, bool verify,
            IProgress<OperationProgress> progress, CancellationToken ct) {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.Steps.Count is < 1 or > Pc410Protocol.SegmentCount)
                throw new ArgumentException("Profile must have 1 to 8 steps.");

            var result = new UploadResult();
            lock (Client.SyncRoot) {
                int total = 3 + Pc410Protocol.SegmentCount * 2;
                int done = 0;
                progress?.Report(new OperationProgress(done, total, "Checking the controller's setpoint limits…"));
                var limitIssues = CheckTargets(profile, ReadSetpointLimits());
                if (limitIssues.Count > 0) throw new ProfileRejectedException(slot, limitIssues, false, false);
                done++;

                progress?.Report(new OperationProgress(done, total, $"Selecting pattern {slot}…"));
                SelectPattern(slot);
                done++;

                // Holdback goes first: if the controller refuses it, the pattern hasn't been touched yet.
                progress?.Report(new OperationProgress(done, total, "Writing holdback…"));
                if (profile.Holdback is double holdback) {
                    try {
                        WriteHoldback(holdback);
                    } catch (HoldbackRejectedException ex) {
                        throw new ProfileRejectedException(slot, [ex.Message], false, false);
                    }
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

                var rejected = new List<string>();
                int step = 1;
                try {
                    for (; step <= Pc410Protocol.SegmentCount && rejected.Count == 0; step++) {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new OperationProgress(done, total, $"Writing step {step}…"));
                        for (int k = 0; k < 3; k++) {
                            var item = planned[(step - 1) * 3 + k];
                            try {
                                Client.Write(item[0], item[1]);
                            } catch (Pc410NakException) {
                                rejected.Add(step <= profile.Steps.Count
                                    ? $"Step {step}: the controller rejected {ValueName(k)} {item[1]}{UnitSuffix(k)} ({item[0]})."
                                    : $"Step {step} is unused, and the controller refused {item[1]} for {item[0]}." +
                                      (Pc410Protocol.IsEnd(item[1]) ? " Set Unused steps to “Write zeros” in Settings." : ""));
                                break;
                            }
                        }
                        done++;
                    }
                } catch (Pc410Exception ex) {
                    throw Interrupted(slot, $"Communication failed while writing step {step}: {ex.Message}");
                }
                if (rejected.Count > 0) throw new ProfileRejectedException(slot, rejected, true, TryClearPattern());

                // Read back. Targets of the profile's steps are always checked, because a controller may silently
                // limit a setpoint instead of rejecting it. Everything else is checked when verification is on.
                for (int n = 1; n <= Pc410Protocol.SegmentCount; n++) {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(done, total, $"Verifying step {n}…"));
                    for (int k = 0; k < 3; k++) {
                        bool isTarget = k == 1 && n <= profile.Steps.Count;
                        if (!verify && !isTarget) continue;
                        var item = planned[(n - 1) * 3 + k];
                        string back;
                        try {
                            back = Client.Read(item[0]).Trim();
                        } catch (Pc410Exception ex) {
                            throw Interrupted(slot, $"Communication failed while checking step {n}: {ex.Message}");
                        }
                        if (ValuesMatch(item[1], back)) continue;
                        if (isTarget)
                            rejected.Add($"Step {n}: target {item[1]} °C was changed to {back} °C by the controller (its setpoint limit is probably lower).");
                        else
                            result.Warnings.Add($"Step {n} {item[0]}: wrote {item[1]}, controller reports {back}.");
                    }
                    done++;
                }
                if (rejected.Count > 0) throw new ProfileRejectedException(slot, rejected, true, TryClearPattern());
                progress?.Report(new OperationProgress(total, total, "Done"));
            }
            return result;
        }

        /// <summary>Clears a partly written pattern after a communication error and builds the exception to throw.</summary>
        private ProfileRejectedException Interrupted(int slot, string issue) {
            bool cleared = TryClearPattern();
            return new ProfileRejectedException(slot, [issue], true, cleared, interrupted: true);
        }

        private static string ValueName(int k) => k switch {
            0 => "ramp rate",
            1 => "target",
            _ => "hold time",
        };

        private string UnitSuffix(int k) => k switch {
            0 => Units.Ramp == RampUnit.PerMinute ? " °C/min" : " °C/s",
            1 => " °C",
            _ => Units.Dwell == DwellUnit.Minutes ? " min" : " s",
        };

        /// <summary>
        /// Empties the selected pattern after a rejected write so it can't run with partial settings:
        /// ends the program at step 1 (END), or writes zeros to step 1 if END isn't accepted.
        /// </summary>
        private bool TryClearPattern() {
            var m = SegmentMnemonics(1);
            try {
                Client.Write(m[0], Pc410Protocol.EndValue);
                return true;
            } catch (Pc410Exception) { }
            try {
                Client.Write(m[0], "0");
                Client.Write(m[1], "0");
                Client.Write(m[2], "0");
                return true;
            } catch (Pc410Exception) {
                return false;
            }
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
                if (ReadAutoTune() == true)
                    throw new Pc410Exception("Auto-tune is running. Wait for it to finish, or stop it on the Control Tuning page, before starting a program.");
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

        /// <summary>Holds the program only if it's running. Returns true when a hold was sent.</summary>
        public bool HoldIfRunning() {
            lock (Client.SyncRoot) {
                if (ReadState() != ProgramState.Running) return false;
                Hold();
                return true;
            }
        }

        /// <summary>
        /// Resumes the program only if it's on hold. Writing Run to an idle controller would start the selected pattern
        /// from the beginning, so a resume must never be sent blind. Returns true when a resume was sent.
        /// </summary>
        public bool ResumeIfHeld() {
            lock (Client.SyncRoot) {
                if (ReadState() != ProgramState.Hold) return false;
                Resume();
                return true;
            }
        }
    }
}
