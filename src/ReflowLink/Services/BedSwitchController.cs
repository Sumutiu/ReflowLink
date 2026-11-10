// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReflowLink.Model;
using ReflowLink.Protocol;

namespace ReflowLink.Services {
    /// <summary>
    /// The bed's program for one run with a single PC410. The PC410 only reads the bed on IN2 and switches it with OUT2,
    /// so ReflowLink runs the bed's program itself, the way a second PC410 would run it:
    /// <list type="bullet">
    /// <item>Its own program (<see cref="OwnProgram"/>): each step ramps to the bed target at the bed's ramp rate, then
    /// holds it for the step's hold time. Like the PC410, it waits while the bed is further from the setpoint than the bed
    /// holdback, and while it's held (Hold, or Wait for IR). Wait for Bed / Wait for IR keep its steps in line with the IR
    /// program's.</item>
    /// <item>A pattern written before the bed had its own ramps: the setpoint follows the IR program. During a step's ramp
    /// it moves from the previous bed target to this step's bed target in proportion to how far the IR setpoint has got,
    /// and during the hold it stays at the bed target.</item>
    /// </list>
    /// </summary>
    public sealed class BedPlan {
        private readonly double[] _ir, _bed, _ramps, _holds;
        private double _holdLeft;

        /// <param name="bedRamps">The bed's ramp rates (°C/s); null to follow the IR program.</param>
        /// <param name="holds">Hold time of each step (s), with <paramref name="bedRamps"/>.</param>
        public BedPlan(IReadOnlyList<double> irTargets, IReadOnlyList<double> bedTargets, double irStart, double bedStart,
            IReadOnlyList<double> bedRamps = null, IReadOnlyList<double> holds = null, double holdback = ReflowProfile.HoldbackDefault) {
            if (irTargets == null || bedTargets == null || irTargets.Count == 0 || irTargets.Count != bedTargets.Count)
                throw new ArgumentException("Every step needs an IR target and a bed target.");
            _ir = [.. irTargets];
            _bed = [.. bedTargets];
            IrStart = irStart;
            BedStart = bedStart;
            if (bedRamps != null) {
                if (bedRamps.Count != _bed.Length || holds == null || holds.Count != _bed.Length || bedRamps.Any(r => !(r > 0)))
                    throw new ArgumentException("Every step needs a bed ramp rate and a hold time.");
                _ramps = [.. bedRamps];
                _holds = [.. holds.Select(h => Math.Max(0, h))];
            }
            Holdback = holdback > 0 ? holdback : ReflowProfile.HoldbackDefault;
            Setpoint = bedStart;
        }

        /// <summary>The plan saved with a pattern, or null if none was saved (written before version 2.1).</summary>
        public static BedPlan From(PatternSync sync, double irStart, double bedStart) =>
            sync == null || !sync.HasTargets ? null
            : sync.HasBedProgram ? new BedPlan(sync.IrTargets, sync.BedTargets, irStart, bedStart, sync.BedRamps, sync.Holds, sync.BedHoldback)
            : new BedPlan(sync.IrTargets, sync.BedTargets, irStart, bedStart);

        public int Steps => _ir.Length;
        public double IrStart { get; }
        public double BedStart { get; }
        public double BedTarget(int step) => _bed[Math.Max(1, Math.Min(Steps, step)) - 1];

        /// <summary>The bed runs its own ramps and holds; false when it follows the IR program (older patterns).</summary>
        public bool OwnProgram => _ramps != null;
        /// <summary>Bed holdback (°C): the program waits while the bed is further than this from the setpoint.</summary>
        public double Holdback { get; }
        /// <summary>Step of the bed's own program (1-based).</summary>
        public int Step { get; private set; } = 1;
        /// <summary>The bed's own program has reached its setpoint in the current step and is holding it.</summary>
        public bool Holding { get; private set; }
        /// <summary>The bed's own program has done its last hold; it stays at the last target until the IR program ends.</summary>
        public bool Finished { get; private set; }
        /// <summary>Setpoint of the bed's own program.</summary>
        public double Setpoint { get; private set; }

        /// <summary>
        /// Runs the bed's own program for <paramref name="dt"/> seconds of process time. Nothing moves while
        /// <paramref name="paused"/>, without a bed reading, or while the bed lags the setpoint by more than the holdback
        /// (during a ramp: behind it in the direction of the ramp; during a hold: either way). At its set temperature it
        /// waits, before the hold time (or before the next step), while <paramref name="mayHold"/> says no (Wait for IR).
        /// </summary>
        public void Advance(double dt, double? bedTemperature, bool paused, Func<int, bool> mayHold = null) {
            if (!OwnProgram || Finished || paused || !(dt > 0)) return;
            if (bedTemperature is not double pv || Lags(pv)) return;
            double left = dt;
            while (left > 1e-9 && !Finished) {
                int i = Step - 1;
                if (!Holding) {
                    double d = _bed[i] - Setpoint, reach = Math.Abs(d) / _ramps[i];
                    if (reach > left) {
                        Setpoint += Math.Sign(d) * _ramps[i] * left;
                        return;
                    }
                    left -= reach;
                    Setpoint = _bed[i];
                    Holding = true;
                    _holdLeft = _holds[i];
                } else if (mayHold != null && !mayHold(Step)) {
                    return; // at the set temperature, waiting for the IR before the hold time
                } else if (_holdLeft > left) {
                    _holdLeft -= left;
                    return;
                } else {
                    left -= _holdLeft;
                    if (Step == Steps) {
                        Finished = true;
                    } else {
                        Step++;
                        Holding = false;
                    }
                }
            }
        }

        private bool Lags(double pv) {
            if (Holding) return Math.Abs(pv - Setpoint) > Holdback;
            // A bed that's ahead of a ramp (still warm, or quick) doesn't stop it.
            return _bed[Step - 1] >= Setpoint ? pv < Setpoint - Holdback : pv > Setpoint + Holdback;
        }

        /// <summary>Bed setpoint while the IR program is in <paramref name="step"/> (1-based) with working setpoint <paramref name="irSp"/>.</summary>
        public double SetpointAt(int step, double? irSp) {
            if (OwnProgram) return Setpoint;
            step = Math.Max(1, Math.Min(Steps, step));
            double irFrom = step == 1 ? IrStart : _ir[step - 2];
            double bedFrom = step == 1 ? BedStart : _bed[step - 2];
            double irTo = _ir[step - 1], bedTo = _bed[step - 1];
            if (irSp is not double sp || Math.Abs(irTo - irFrom) < 0.5) return bedTo;
            double f = (sp - irFrom) / (irTo - irFrom);
            f = Math.Max(0, Math.Min(1, f));
            return bedFrom + f * (bedTo - bedFrom);
        }
    }

    /// <summary>What the coordinator needs to know about a bed regulated by ReflowLink (single PC410).</summary>
    public readonly struct VirtualBedState(double? pv, double? sp, int? step) {
        public double? Pv { get; } = pv;
        public double? Sp { get; } = sp;
        /// <summary>Step of the bed's own program; null when the bed follows the IR program (always in the IR's step).</summary>
        public int? Step { get; } = step;
    }

    /// <summary>PI regulator for the bed: heating only, output 0..100 %.</summary>
    public sealed class BedRegulator {
        private double _integral;

        /// <summary>Full power this far below the setpoint (°C).</summary>
        public double Band { get; set; } = 15;
        /// <summary>Integral time (s); 0 = proportional only.</summary>
        public double IntegralSeconds { get; set; } = 240;

        public void Reset() => _integral = 0;

        public double Update(double setpoint, double pv, double dt) {
            double e = setpoint - pv;
            double p = 100 * e / Math.Max(0.1, Band);
            if (IntegralSeconds > 0 && dt > 0) {
                double next = _integral + 100 * e / Math.Max(0.1, Band) * dt / IntegralSeconds;
                // Don't wind up while the output is already at its limit in the same direction.
                bool saturated = (p + next > 100 && e > 0) || (p + next < 0 && e < 0);
                if (!saturated) _integral = next;
                _integral = Clamp(_integral, 0, 100);
            } else if (IntegralSeconds <= 0) {
                _integral = 0;
            }
            return Clamp(p + _integral, 0, 100);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>
    /// Turns a demand (0..100 %) into on/off switching over a fixed cycle. Pulses shorter than the minimum switch time
    /// are dropped (or the gap filled), so OUT2 never chatters.
    /// </summary>
    public sealed class TimeProportioner {
        private double _cycleStart = double.NaN;

        public double CycleSeconds { get; set; } = 10;
        public double MinSwitchSeconds { get; set; } = 1;

        public void Reset() => _cycleStart = double.NaN;

        /// <summary>Whether the output is on at process time <paramref name="now"/> (s) for <paramref name="demand"/> %.</summary>
        public bool On(double now, double demand) {
            double cycle = Math.Max(1, CycleSeconds);
            if (double.IsNaN(_cycleStart) || now < _cycleStart) _cycleStart = now;
            if (now - _cycleStart >= cycle) _cycleStart += Math.Floor((now - _cycleStart) / cycle) * cycle;
            double onTime = Math.Max(0, Math.Min(100, demand)) / 100 * cycle;
            if (onTime < MinSwitchSeconds) onTime = 0;
            else if (cycle - onTime < MinSwitchSeconds) onTime = cycle;
            return now - _cycleStart < onTime;
        }
    }

    /// <summary>
    /// Single PC410 bed control. The top PC410 reads the bed thermocouple on IN2 (P2), and its OUT2 relay passes the bed
    /// controller's output to the bed SSR only while closed. ReflowLink regulates the bed by switching OUT2, writing alarm
    /// 2 (A2 = 001 on, A2 = 500 off), only when the state has to change.
    /// <para>
    /// The bed is switched off whenever ReflowLink can't regulate it: no program, Stop, no bed targets for the pattern,
    /// no bed reading, step not reported, above the bed limit, or the link lost. What software can't cover (ReflowLink
    /// crashing, the USB cable pulled) is why the bed controller behind OUT2, or a thermostat, must stay as a hardware limit.
    /// </para>
    /// </summary>
    public sealed class BedSwitchController {
        /// <summary>
        /// Readings older than this don't count (seconds of real time). Until then a missed reading (bed temperature, step
        /// or program state) is bridged with the last good one, so a single glitch on the cable doesn't switch OUT2 off and
        /// straight back on (two needless A2 writes and relay clicks).
        /// </summary>
        private const double StaleSeconds = 5;
        /// <summary>A bed reading outside this range is treated as a sensor fault.</summary>
        private const double MinValid = -50, MaxValid = 600;

        private readonly DeviceService _ir;
        private readonly AppSettings _settings;
        private readonly BedRegulator _regulator = new();
        private readonly TimeProportioner _proportioner = new();
        private readonly DateTime _origin = DateTime.Now;

        private BedPlan _plan;
        private bool _wasActive, _halted, _paired, _pending, _offQueued, _failureReported, _sensorLostReported;
        // Held off by SwitchOffAsync while the user decides whether to disconnect, exit or change the bed hardware.
        private bool _holdOff;
        // Stop pressed: stays off until the program has been seen idle, even if a start that was already on its way shows
        // up as running first. A new run can only be set up after that.
        private bool _stopHeld;
        // The plan came from a start in ReflowLink (a checked pattern), so what the run shows about segments can be learned.
        private bool _planFromBegin;
        private int _pairedEvidence;
        // Set when bed control is switched on: a run already in progress then isn't taken over (it starts with the next run).
        private bool _freshEnable;
        private string _noPlanReason = "No bed targets";
        private double _lastProcessTime = double.NaN;
        private int _failures;
        private Task _pendingTask;
        private DateTime _lastBedReading = DateTime.MinValue, _retryAfter = DateTime.MinValue;
        // Last step and program state the controller reported, for bridging a missed reading.
        private int? _lastStep;
        private DateTime _lastStepAt = DateTime.MinValue, _lastStateAt = DateTime.MinValue;
        private bool _enabled;

        public BedSwitchController(DeviceService ir, AppSettings settings) {
            _ir = ir;
            _settings = settings;
            ir.TelemetryReceived += OnTelemetry;
            ir.StateChanged += (s, e) => OnLinkChanged();
        }

        /// <summary>Single PC410 bed control is selected in Settings.</summary>
        public bool Enabled {
            get => _enabled;
            set {
                if (_enabled == value) return;
                _enabled = value;
                Reset();
                _freshEnable = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Profile looked up for a pattern started outside ReflowLink, to check the saved bed targets still fit it.</summary>
        public Func<int, ReflowProfile> KnownPattern { get; set; }

        /// <summary>
        /// This controller was seen counting ramp and dwell as separate segments in an earlier run, so a step is two
        /// segments from the start of a run (instead of only once a segment above the number of steps has been seen).
        /// </summary>
        public bool PairedHint { get; set; }

        /// <summary>Raised when a run shows that the controller counts ramp and dwell as separate segments.</summary>
        public event Action PairedDetected;

        public BedPlan Plan => _plan;
        /// <summary>Bed temperature (P2) from the latest poll; null when missing, implausible or stale.</summary>
        public double? BedTemperature { get; private set; }
        /// <summary>Bed setpoint while regulating; null otherwise.</summary>
        public double? Setpoint { get; private set; }
        /// <summary>Regulator demand (%), 0 while the bed is held off.</summary>
        public double Demand { get; private set; }
        /// <summary>OUT2 should be on now.</summary>
        public bool WantOn { get; private set; }
        /// <summary>OUT2 as last written or read back: true = on, false = off, null = unknown.</summary>
        public bool? Out2 { get; private set; }
        /// <summary>Alarm 2 value last read from the controller.</summary>
        public double? Alarm2 { get; private set; }
        /// <summary>Why the bed is on or off, for the interface.</summary>
        public string Reason { get; private set; } = "Not connected";
        public int SessionWrites { get; private set; }
        public DateTime? LastWrite { get; private set; }
        public string LastError { get; private set; } = "";
        /// <summary>Latest bed reading in the form the Dashboard and the CSV log use (state and step are the IR's).</summary>
        public Telemetry Last { get; private set; }

        /// <summary>Raised on the UI thread after each poll, with the bed reading.</summary>
        public event Action<Telemetry> TelemetryReady;
        /// <summary>Raised when anything shown about the bed changes.</summary>
        public event Action Changed;
        /// <summary>Raised when an A2 write failed (once per streak of failures).</summary>
        public event Action<string> CommandFailed;
        /// <summary>Raised once per run when the bed reading is lost while regulating.</summary>
        public event Action SensorLost;

        /// <summary>
        /// Bed side for Wait for Bed / Wait for IR: its temperature, setpoint and step (null: the IR's step, for a bed that
        /// follows the IR program), or null when it isn't being regulated.
        /// </summary>
        public VirtualBedState? VirtualBed() =>
            _enabled && _plan != null && BedTemperature.HasValue && Setpoint.HasValue
                ? new VirtualBedState(BedTemperature, Setpoint, _plan.OwnProgram ? _plan.Step : null)
                : null;

        /// <summary>The bed's own program is held by Wait for IR (set by the <see cref="SyncCoordinator"/>).</summary>
        public bool VirtualHold { get; set; }

        /// <summary>
        /// Whether the bed's own program may do a step's hold time and go on (Wait for IR, see
        /// <see cref="SyncCoordinator.BedMayStartHold"/>); null: always.
        /// </summary>
        public Func<int, bool> MayStartHold { get; set; }

        /// <summary>
        /// The IR program is on hold only because it waits for the bed (Wait for Bed), so the bed's program keeps going.
        /// Any other hold (the user's) holds the bed's program too.
        /// </summary>
        public Func<bool> IrHeldForBed { get; set; }

        /// <summary>Sets the plan for the run being started from ReflowLink (also fine after its first poll arrived).</summary>
        public void Begin(BedPlan plan) {
            if (!_enabled || _stopHeld) return;
            _plan = plan;
            _planFromBegin = plan != null;
            _halted = false;
            Changed?.Invoke();
        }

        /// <summary>Stop pressed: forget the run, switch the bed off now and keep it off until the next run starts.</summary>
        public void Stop() {
            if (!_enabled) return;
            _halted = true;
            _stopHeld = true;
            _plan = null;
            WantOn = false;
            Setpoint = null;
            Demand = 0;
            Reason = "Stopped";
            RequestOff();
            Changed?.Invoke();
        }

        /// <summary>
        /// Switches OUT2 off and waits for the controller to confirm, without ending the run (used before disconnecting,
        /// closing ReflowLink or changing the bed hardware). The bed stays off until <see cref="ResumeAfterSwitchOff"/>, so
        /// regulation carries on unchanged if the user then cancels. Returns false if the switch-off couldn't be confirmed.
        /// </summary>
        public async Task<bool> SwitchOffAsync(int timeoutMs = 4000) {
            _holdOff = true;
            WantOn = false;
            if (!_ir.IsConnected) return false;
            try {
                // A write still on its way (possibly "on") must land first, or it could overtake this one. If it doesn't
                // land in time, the result can't be confirmed.
                bool earlierLanded = true;
                var pending = _pendingTask;
                if (pending != null && !pending.IsCompleted)
                    earlierLanded = await Task.WhenAny(pending, Task.Delay(timeoutMs)) == pending && pending.Status == TaskStatus.RanToCompletion;
                // Write "off" and read it back inside the same bus lock.
                var t = _ir.RunAsync(d => {
                    d.SetBedSwitch(false);
                    return d.ReadBedAlarm() >= Pc410Device.BedOffThreshold;
                });
                if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t) return false;
                bool off = await t;
                NoteWrite(false);
                return earlierLanded && off;
            } catch (Exception ex) {
                LastError = DeviceService.Describe(ex.GetBaseException());
                return false;
            } finally {
                Changed?.Invoke();
            }
        }

        /// <summary>The user cancelled after <see cref="SwitchOffAsync"/>: regulate the bed again.</summary>
        public void ResumeAfterSwitchOff() {
            if (!_holdOff) return;
            _holdOff = false;
            Changed?.Invoke();
        }

        /// <summary>True while the bed is held off by <see cref="SwitchOffAsync"/>.</summary>
        public bool HeldOff => _holdOff;

        private void Reset() {
            _plan = null;
            _wasActive = _halted = _holdOff = _stopHeld = _planFromBegin = false;
            _paired = PairedHint;
            _pairedEvidence = 0;
            _offQueued = _failureReported = _sensorLostReported = false;
            _lastStep = null;
            _lastProcessTime = double.NaN;
            VirtualHold = false;
            _failures = 0;
            _retryAfter = DateTime.MinValue;
            _regulator.Reset();
            _proportioner.Reset();
            BedTemperature = Setpoint = null;
            Demand = 0;
            WantOn = false;
            Out2 = null;
            Alarm2 = null;
            Last = null;
            _noPlanReason = "No bed targets";
            Reason = _ir.IsConnected ? "Idle" : "Not connected";
        }

        private void OnLinkChanged() {
            if (!_enabled) return;
            if (_ir.State == LinkStatus.Connected) {
                // A fresh link: earlier write failures don't delay switching any more.
                _retryAfter = DateTime.MinValue;
                _failures = 0;
                _failureReported = false;
            } else if (_ir.State == LinkStatus.NoResponse) {
                // Polls stopped arriving: try to switch the bed off while the controller might still listen.
                WantOn = false;
                Reason = "No response";
                if (Out2 != false) RequestOff();
                Changed?.Invoke();
            }
            if (!_ir.IsConnected) {
                // Nothing can be switched any more; OUT2 stays as it was (the hardware limit covers this).
                _holdOff = false;
                Out2 = null;
                Alarm2 = null;
                _wasActive = false;
                _plan = null;
                BedTemperature = Setpoint = null;
                Reason = "Not connected";
                Changed?.Invoke();
            }
        }

        private void OnTelemetry(Telemetry t) {
            if (!_enabled) return;
            var s = _settings.Bed.Switching;
            _regulator.Band = s.ProportionalBand;
            _regulator.IntegralSeconds = s.IntegralSeconds;
            _proportioner.CycleSeconds = s.CycleSeconds;

            double now = (t.Time - _origin).TotalSeconds * Math.Max(1, _ir.TimeScale);
            double dt = double.IsNaN(_lastProcessTime) ? 0 : Math.Max(0, Math.Min(5 * Math.Max(1, _ir.TimeScale), now - _lastProcessTime));
            _lastProcessTime = now;

            if (t.Pv2 is double p2 && p2 > MinValid && p2 < MaxValid) {
                BedTemperature = p2;
                _lastBedReading = DateTime.Now;
            } else if ((DateTime.Now - _lastBedReading).TotalSeconds > StaleSeconds) {
                BedTemperature = null;
            }
            // A reading taken before the last write finished is out of date.
            bool freshA2 = t.Alarm2.HasValue && (LastWrite == null || t.Time > LastWrite.Value);
            if (freshA2) Alarm2 = t.Alarm2;

            // A poll whose program state couldn't be read says nothing about starts and ends: keep the run as it was. A
            // short gap is bridged; after that the bed isn't heated on a guess.
            bool stateKnown = t.State != ProgramState.Unknown;
            if (stateKnown) _lastStateAt = DateTime.Now;
            bool stateFresh = stateKnown || (DateTime.Now - _lastStateAt).TotalSeconds <= StaleSeconds;
            bool active = stateKnown ? t.State is ProgramState.Running or ProgramState.Hold : _wasActive;
            if (stateKnown) {
                if (_freshEnable) {
                    // Switched on (or ReflowLink started) during a run: don't take it over halfway.
                    _freshEnable = false;
                    _wasActive = active;
                    if (active) {
                        _plan = null;
                        _noPlanReason = "From the next run";
                    }
                }
                if (active && !_wasActive) StartRun(t);
                if (!active && _wasActive) {
                    _plan = null; // program ended or stopped
                    _planFromBegin = false;
                    _halted = false;
                    _lastStep = null;
                }
                if (!active) _stopHeld = false;
                _wasActive = active;
            }

            // Which step the IR program is in (some controllers count ramp and dwell as separate segments).
            int? step = null;
            if (_plan != null) {
                // Segments above the step count (up to twice as many) mean ramp and dwell are counted separately. A few
                // readings are needed, so one bad reading can't change how steps are counted.
                int seg = t.Segment ?? 0;
                if (!_paired && seg > _plan.Steps && seg <= 2 * _plan.Steps && ++_pairedEvidence >= 3) {
                    _paired = true;
                    if (!PairedHint && _planFromBegin) PairedDetected?.Invoke();
                }
                step = SyncCoordinator.StepOf(t.Segment, _plan.Steps, _paired);
                if (step.HasValue) {
                    _lastStep = step;
                    _lastStepAt = DateTime.Now;
                } else if (active && _lastStep.HasValue && (DateTime.Now - _lastStepAt).TotalSeconds <= StaleSeconds) {
                    step = _lastStep; // a missed SE reading: the program is still in the step it last reported
                }
            }

            // The bed's own program runs while the IR program does. It's paused with it when the user holds the program (a
            // second PC410 would be held too), but not when the IR is only held to wait for the bed; and by Wait for IR.
            if (active && _plan != null && !_halted && stateFresh) {
                bool userHold = t.State == ProgramState.Hold && !(IrHeldForBed?.Invoke() ?? false);
                _plan.Advance(dt, BedTemperature, userHold || VirtualHold, MayStartHold);
            }

            var why = _holdOff ? "Switched off"
                : !stateFresh ? "Program state unknown"
                : Decide(_ir.State == LinkStatus.Connected, active, _halted, _plan, step, BedTemperature, _settings.Bed.MaxTemperature);
            if (why == "No bed targets") why = _noPlanReason;
            if (why == null) {
                Setpoint = _plan.SetpointAt(step.Value, t.Sp);
                Demand = _regulator.Update(Setpoint.Value, BedTemperature.Value, dt);
                // Waiting for the IR (Wait for IR) at or above its set temperature: no power at all until the IR is there too.
                if (VirtualHold && BedTemperature.Value >= Setpoint.Value) Demand = 0;
                WantOn = _proportioner.On(now, Demand);
                Reason = WantOn ? "Heating" : "Regulating";
            } else {
                Setpoint = active && _plan != null && step.HasValue ? _plan.SetpointAt(step.Value, t.Sp) : null;
                Demand = 0;
                WantOn = false;
                Reason = why;
            }

            if (active && _plan != null && !_halted && BedTemperature == null && !_sensorLostReported) {
                _sensorLostReported = true;
                SensorLost?.Invoke();
            }

            // Trust what the controller reports, then write only when OUT2 has to change.
            if (!_pending && freshA2) Out2 = t.Alarm2.Value < Pc410Device.BedOffThreshold;
            if (!_pending && Out2 != WantOn && DateTime.Now >= _retryAfter) Write(WantOn);

            Last = new Telemetry {
                Time = t.Time,
                Pv = BedTemperature,
                Sp = Setpoint,
                Op = Demand,
                State = t.State,
                // The bed's own step when ReflowLink runs its program; otherwise it's in the IR's step.
                Segment = _plan?.OwnProgram == true && active ? _plan.Step : t.Segment,
            };
            TelemetryReady?.Invoke(Last);
            Changed?.Invoke();
        }

        /// <summary>A run has started (from ReflowLink or the front panel): fresh regulation, and the pattern's saved plan.</summary>
        private void StartRun(Telemetry t) {
            _regulator.Reset();
            _proportioner.Reset();
            _paired = PairedHint;
            _pairedEvidence = 0;
            _lastStep = null;
            VirtualHold = false;
            // A start that overtook a Stop doesn't switch the bed back on.
            _halted = _stopHeld;
            if (_stopHeld) return;
            _sensorLostReported = false;
            _noPlanReason = "No bed targets";
            if (_plan != null) return; // set by Begin
            int? pattern = t.Pattern ?? _ir.ActivePattern;
            if (pattern is not int slot) return;
            var sync = _settings.SyncFor(slot);
            if (sync == null || !sync.HasTargets) return;
            // Started on the front panel: only use the saved bed targets if the pattern is known to still hold the profile
            // they belong to (read or written this session). Otherwise it may have been changed elsewhere.
            var known = KnownPattern?.Invoke(slot);
            if (known == null || !sync.Matches(known)) {
                _noPlanReason = "Pattern not checked";
                return;
            }
            if (sync.BedTargets.Any(b => b > _settings.Bed.MaxTemperature)) {
                _noPlanReason = "Bed target above limit";
                return;
            }
            _plan = BedPlan.From(sync, t.Pv ?? sync.IrTargets[0], BedTemperature ?? t.Pv ?? sync.BedTargets[0]);
            _planFromBegin = false;
        }

        /// <summary>
        /// Why the bed must stay off, or null when it can be regulated. Pure, so it can be tested.
        /// </summary>
        public static string Decide(bool linked, bool active, bool halted, BedPlan plan, int? step, double? bedTemperature, double maxTemperature) {
            if (!linked) return "Not connected";
            if (halted) return "Stopped";
            if (!active) return "Idle";
            if (plan == null) return "No bed targets";
            if (bedTemperature is not double bed) return "No bed reading (P2)";
            if (step == null) return "Step not reported";
            if (bed >= maxTemperature) return $"Above {maxTemperature:0} °C limit";
            return null;
        }

        private void RequestOff() {
            if (_pending) {
                _offQueued = true;
                return;
            }
            if (_ir.IsConnected) Write(false);
        }

        private async void Write(bool on) {
            _pending = true;
            try {
                var task = _ir.RunAsync(d => d.SetBedSwitch(on));
                _pendingTask = task;
                await task;
                NoteWrite(on);
                _failureReported = false;
                _failures = 0;
                LastError = "";
            } catch (Exception ex) {
                Out2 = null; // unknown: written again once the retry delay has passed
                _failures++;
                _retryAfter = DateTime.Now.AddSeconds(Math.Min(30, 2 << Math.Min(4, _failures - 1)));
                LastError = DeviceService.Describe(ex.GetBaseException());
                if (!_failureReported) {
                    _failureReported = true;
                    CommandFailed?.Invoke((on ? "Couldn't switch the bed on (A2): " : "Couldn't switch the bed off (A2): ") + LastError);
                }
            } finally {
                _pending = false;
                if (_offQueued) {
                    _offQueued = false;
                    if (_ir.IsConnected) Write(false);
                }
                Changed?.Invoke();
            }
        }

        private void NoteWrite(bool on) {
            Out2 = on;
            Alarm2 = on ? 1 : Pc410Device.BedOffThreshold; // what was just written, until the next read
            SessionWrites++;
            _settings.Bed.Switching.TotalWrites++;
            LastWrite = DateTime.Now;
        }
    }
}
