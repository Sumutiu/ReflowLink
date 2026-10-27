// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using ReflowLink.Model;
using ReflowLink.Protocol;

namespace ReflowLink.Services {
    /// <summary>
    /// The bed's setpoint path for one run with a single PC410. The bed has no program of its own, so its setpoint follows
    /// the IR program: during a step's ramp it moves from the previous bed target to this step's bed target in proportion
    /// to how far the IR setpoint has got, and during the hold it stays at the bed target.
    /// </summary>
    public sealed class BedPlan {
        private readonly double[] _ir, _bed;

        public BedPlan(IReadOnlyList<double> irTargets, IReadOnlyList<double> bedTargets, double irStart, double bedStart) {
            if (irTargets == null || bedTargets == null || irTargets.Count == 0 || irTargets.Count != bedTargets.Count)
                throw new ArgumentException("Every step needs an IR target and a bed target.");
            _ir = [.. irTargets];
            _bed = [.. bedTargets];
            IrStart = irStart;
            BedStart = bedStart;
        }

        /// <summary>The plan saved with a pattern, or null if none was saved (written before version 2.1).</summary>
        public static BedPlan From(PatternSync sync, double irStart, double bedStart) =>
            sync != null && sync.HasTargets ? new BedPlan(sync.IrTargets, sync.BedTargets, irStart, bedStart) : null;

        public int Steps => _ir.Length;
        public double IrStart { get; }
        public double BedStart { get; }
        public double BedTarget(int step) => _bed[Math.Max(1, Math.Min(Steps, step)) - 1];

        /// <summary>Bed setpoint while the IR program is in <paramref name="step"/> (1-based) with working setpoint <paramref name="irSp"/>.</summary>
        public double SetpointAt(int step, double? irSp) {
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
        /// <summary>Readings older than this don't count (seconds of real time).</summary>
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
        // Set when bed control is switched on: a run already in progress then isn't taken over (it starts with the next run).
        private bool _freshEnable;
        private string _noPlanReason = "No bed targets";
        private double _lastProcessTime = double.NaN;
        private int _failures;
        private System.Threading.Tasks.Task _pendingTask;
        private DateTime _lastBedReading = DateTime.MinValue, _retryAfter = DateTime.MinValue;
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

        /// <summary>Bed side for Wait for Bed: its temperature and setpoint, or null when it isn't being regulated.</summary>
        public (double? Pv, double? Sp)? VirtualBed() =>
            _enabled && _plan != null && BedTemperature.HasValue && Setpoint.HasValue ? (BedTemperature, Setpoint) : null;

        /// <summary>Sets the plan for the run being started from ReflowLink (also fine after its first poll arrived).</summary>
        public void Begin(BedPlan plan) {
            if (!_enabled) return;
            _plan = plan;
            _halted = false;
            Changed?.Invoke();
        }

        /// <summary>Stop pressed: forget the run, switch the bed off now and keep it off until the next run starts.</summary>
        public void Stop() {
            if (!_enabled) return;
            _halted = true;
            _plan = null;
            WantOn = false;
            Setpoint = null;
            Demand = 0;
            Reason = "Stopped";
            RequestOff();
            Changed?.Invoke();
        }

        /// <summary>
        /// Switches OUT2 off and waits for the controller to confirm (used before disconnecting, closing ReflowLink or
        /// changing the bed hardware). Returns false if that couldn't be confirmed.
        /// </summary>
        public bool SwitchOffNow(int timeoutMs = 4000) {
            _halted = true;
            _plan = null;
            WantOn = false;
            if (!_ir.IsConnected) return false;
            try {
                // A write still on its way (possibly "on") must land first, or it could overtake this one. If it doesn't
                // land in time, the result can't be confirmed.
                bool earlierLanded = true;
                var pending = _pendingTask;
                if (pending != null && !pending.IsCompleted) {
                    try { earlierLanded = pending.Wait(timeoutMs); } catch (AggregateException) { /* failed: it won't land */ }
                }
                // Write "off" and read it back inside the same bus lock.
                var t = _ir.RunAsync(d => {
                    d.SetBedSwitch(false);
                    return d.ReadBedAlarm() >= Pc410Device.BedOffThreshold;
                });
                if (!t.Wait(timeoutMs)) return false;
                NoteWrite(false);
                return earlierLanded && t.Result;
            } catch (Exception ex) {
                LastError = DeviceService.Describe(ex.GetBaseException());
                return false;
            } finally {
                Changed?.Invoke();
            }
        }

        private void Reset() {
            _plan = null;
            _wasActive = _halted = _paired = false;
            _offQueued = _failureReported = _sensorLostReported = false;
            _lastProcessTime = double.NaN;
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

            // A poll whose program state couldn't be read says nothing about starts and ends: keep the run as it was, but
            // don't heat on it.
            bool stateKnown = t.State != ProgramState.Unknown;
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
                    _halted = false;
                }
                _wasActive = active;
            }

            // Which step the IR program is in (some controllers count ramp and dwell as separate segments).
            int? step = null;
            if (_plan != null) {
                if ((t.Segment ?? 0) > _plan.Steps) _paired = true;
                step = SyncCoordinator.StepOf(t.Segment, _plan.Steps, _paired);
            }

            var why = !stateKnown ? "Program state unknown"
                : Decide(_ir.State == LinkStatus.Connected, active, _halted, _plan, step, BedTemperature, _settings.Bed.MaxTemperature);
            if (why == "No bed targets") why = _noPlanReason;
            if (why == null) {
                Setpoint = _plan.SetpointAt(step.Value, t.Sp);
                Demand = _regulator.Update(Setpoint.Value, BedTemperature.Value, dt);
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
                Segment = t.Segment,
            };
            TelemetryReady?.Invoke(Last);
            Changed?.Invoke();
        }

        /// <summary>A run has started (from ReflowLink or the front panel): fresh regulation, and the pattern's saved plan.</summary>
        private void StartRun(Telemetry t) {
            _regulator.Reset();
            _proportioner.Reset();
            _paired = false;
            _halted = false;
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
