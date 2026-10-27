// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using ReflowLink.Model;
using ReflowLink.Protocol;

namespace ReflowLink.Services {
    /// <summary>What the coordinator needs to know about one controller.</summary>
    public readonly struct SyncSide(ProgramState state, double? pv, double? sp, int? step) {
        public ProgramState State { get; } = state;
        public double? Pv { get; } = pv;
        public double? Sp { get; } = sp;
        /// <summary>Current program step (1-based); null when the controller doesn't report it.</summary>
        public int? Step { get; } = step;

        public bool Active => State is ProgramState.Running or ProgramState.Hold;

        /// <summary>True when the temperature is further from the setpoint than the holdback band.</summary>
        public bool Lags(double holdback) => Pv is double pv && Sp is double sp && Math.Abs(pv - sp) > holdback;
    }

    /// <summary>
    /// Keeps the top IR and bed heat programs in step. The two PC410s can't talk to each other, so ReflowLink holds
    /// whichever one is ahead:
    /// <list type="bullet">
    /// <item>Wait for Bed on a step: the IR doesn't get ahead of the bed in that step. While the bed is in the step,
    /// the IR is held if it has already moved on to a later step, or if both are in the step and the bed is further
    /// from its setpoint than the bed holdback.</item>
    /// <item>Wait for IR on a step: the same for the bed, using the IR holdback.</item>
    /// </list>
    /// So a toggle goes on the step where the heaters drift apart (for example the step where the bed has the longer
    /// ramp). A program held by the user is never resumed automatically.
    /// <para>With a single PC410 (<see cref="VirtualBed"/>) only Wait for Bed applies: the IR is held while the bed,
    /// regulated by ReflowLink, lags its setpoint.</para>
    /// </summary>
    public sealed class SyncCoordinator {
        private sealed class Side(DeviceService device) {
            public readonly DeviceService Device = device;
            /// <summary>The coordinator put this controller on hold (as opposed to the user).</summary>
            public bool AutoHeld;
            public bool Pending;
        }

        private readonly Side _ir, _bed;
        private bool _wasPairRun, _bedLostReported, _irLostReported;
        // Read from the command threads: a stop, a user hold or a lost link must cancel commands already queued.
        private volatile bool _userHold, _latched;
        private volatile int _generation;

        public SyncCoordinator(DeviceService ir, DeviceService bed) {
            _ir = new Side(ir);
            _bed = new Side(bed);
            ir.TelemetryReceived += t => Evaluate();
            bed.TelemetryReceived += t => Evaluate();
            bed.StateChanged += (s, e) => OnLinkChanged(bed);
            ir.StateChanged += (s, e) => OnLinkChanged(ir);
        }

        /// <summary>Bed heat control is switched on in Settings.</summary>
        public bool Enabled { get; set; }

        /// <summary>Wait for Bed / Wait for IR choices of the running pattern; null when there are none.</summary>
        public PatternSync Sync { get; set; }

        /// <summary>
        /// Single PC410: the bed's temperature and setpoint come from here instead of a second controller, and the bed is in
        /// the same step as the IR. Only Wait for Bed applies (the bed has no program that could be held). Null with a
        /// second PC410.
        /// </summary>
        public Func<(double? Pv, double? Sp)?> VirtualBed { get; set; }

        /// <summary>
        /// The user held or is stopping the program: leave both controllers alone until it's cleared. Setting it also
        /// cancels any hold or resume the coordinator has queued.
        /// </summary>
        public bool UserHold {
            get => _userHold;
            set {
                _userHold = value;
                _generation++;
                if (value) _ir.AutoHeld = _bed.AutoHeld = false;
            }
        }

        public bool IrWaiting => _ir.AutoHeld;
        public bool BedWaiting => _bed.AutoHeld;

        public string WaitingText => _ir.AutoHeld ? "IR waiting for bed" : _bed.AutoHeld ? "Bed waiting for IR" : "";

        /// <summary>Segments seem to count ramp and dwell separately (1..16), so a step is two segments.</summary>
        public bool PairedSegments { get; private set; }

        /// <summary>The controllers don't report their current step, so the per-step choices can't be applied.</summary>
        public bool StepUnknown { get; private set; }

        /// <summary>Raised on the UI thread when a controller is held or resumed by the coordinator.</summary>
        public event Action Changed;

        /// <summary>Raised once when the bed controller stops answering during a run with bed heat control.</summary>
        public event Action BedLost;

        /// <summary>Raised once when the top IR controller stops answering during a run with bed heat control.</summary>
        public event Action IrLost;

        /// <summary>Raised when a hold or resume command failed.</summary>
        public event Action<string> CommandFailed;

        /// <summary>Forgets automatic holds and re-arms the coordinator (before a new run, or after the program ended).</summary>
        public void Reset() {
            _generation++;
            _ir.AutoHeld = _bed.AutoHeld = false;
            PairedSegments = false;
            _bedLostReported = _irLostReported = false;
            _latched = false;
            _userHold = false;
            Changed?.Invoke();
        }

        /// <summary>
        /// Called when a new run starts (also from the front panel). Clears a latch or user hold left from the previous
        /// run, unless the coordinator still holds a controller.
        /// </summary>
        public void RunStarted() {
            if (_ir.AutoHeld || _bed.AutoHeld) return;
            _latched = false;
            _userHold = false;
            _bedLostReported = _irLostReported = false;
            PairedSegments = false;
        }

        /// <summary>Stops the coordinator from sending anything until <see cref="Reset"/> (used when the program is stopped).</summary>
        public void Halt() {
            _latched = true;
            _generation++;
            _ir.AutoHeld = _bed.AutoHeld = false;
            Changed?.Invoke();
        }

        /// <summary>
        /// Maps a reported segment to a program step. Some controllers count ramp and dwell as separate segments
        /// (1..16); that's assumed once a segment above the number of steps has been seen.
        /// </summary>
        public static int? StepOf(int? segment, int steps, bool pairedSegments) {
            if (segment is not int s || s <= 0) return null;
            return pairedSegments || (steps > 0 && s > steps) ? (s + 1) / 2 : s;
        }

        /// <summary>Decides which controller should be held. Pure function, so it can be tested.</summary>
        public static (bool HoldIr, bool HoldBed) Decide(PatternSync sync, SyncSide ir, SyncSide bed) {
            if (sync == null || !sync.Any || !ir.Active || !bed.Active) return (false, false);
            if (ir.Step is not int irStep || bed.Step is not int bedStep) return (false, false);
            bool holdIr = sync.WaitForBed(bedStep) && (irStep > bedStep || (irStep == bedStep && bed.Lags(sync.BedHoldback)));
            bool holdBed = sync.WaitForIr(irStep) && (bedStep > irStep || (bedStep == irStep && ir.Lags(sync.IrHoldback)));
            return (holdIr, holdBed);
        }

        private void Evaluate() {
            if (_latched) return;
            var ir = _ir.Device.Last;
            var virtualBed = VirtualBed;
            bool single = virtualBed != null;
            var bed = single ? null : _bed.Device.Last;
            var virt = single ? virtualBed() : null;
            bool linked = Enabled && _ir.Device.State == LinkStatus.Connected && ir != null
                && (single ? virt.HasValue : _bed.Device.State == LinkStatus.Connected && bed != null);
            if (linked) _wasPairRun = (ir.State is ProgramState.Running or ProgramState.Hold) && (single || bed.State is ProgramState.Running or ProgramState.Hold);
            if (!linked || _userHold || Sync == null) {
                StepUnknown = false;
                // Bed heat switched off, a controller disconnected on purpose, or no waits: let anything we held carry on.
                // (A controller that stopped answering latches the coordinator instead, see OnLinkChanged.)
                if (!_userHold) ReleaseAll();
                return;
            }

            int steps = Sync.Steps;
            if ((ir.Segment ?? 0) > steps || (!single && (bed.Segment ?? 0) > steps)) PairedSegments = true;
            var irSide = new SyncSide(ir.State, ir.Pv, ir.Sp, StepOf(ir.Segment, steps, PairedSegments));
            // A single PC410's bed is always in the IR's step and "runs" exactly when the IR program does.
            var bedSide = single
                ? new SyncSide(ir.State, virt.Value.Pv, virt.Value.Sp, irSide.Step)
                : new SyncSide(bed.State, bed.Pv, bed.Sp, StepOf(bed.Segment, steps, PairedSegments));
            StepUnknown = Sync.Any && irSide.Active && bedSide.Active && (irSide.Step == null || bedSide.Step == null);

            var (holdIr, holdBed) = Decide(Sync, irSide, bedSide);
            Apply(_ir, holdIr, ir.State);
            if (!single) Apply(_bed, holdBed, bed.State);
        }

        private void ReleaseAll() {
            if (_ir.AutoHeld) Apply(_ir, false, _ir.Device.Last?.State ?? ProgramState.Unknown);
            if (_bed.AutoHeld) Apply(_bed, false, _bed.Device.Last?.State ?? ProgramState.Unknown);
        }

        private async void Apply(Side side, bool hold, ProgramState state) {
            if (side.Pending || _latched || _userHold) return;
            // Hold again if someone else resumed a controller the coordinator was holding.
            bool start = hold && state == ProgramState.Running;
            bool resume = !hold && side.AutoHeld;
            if (!start && !resume) return;
            if (resume && state != ProgramState.Hold) {
                // Already running again, stopped or finished: nothing to resume.
                side.AutoHeld = false;
                Changed?.Invoke();
                return;
            }
            if (side.Device.State != LinkStatus.Connected) {
                // Can't be resumed from here any more; stop showing it as waiting.
                if (resume) {
                    side.AutoHeld = false;
                    Changed?.Invoke();
                }
                return;
            }
            int generation = _generation;
            side.Pending = true;
            try {
                // Re-check inside the controller's lock: a Stop, a user hold or a lost link may have happened since this
                // decision, and Run sent to an idle controller would restart its pattern.
                bool sent = await side.Device.RunAsync(d => {
                    if (generation != _generation || _latched || _userHold) return false;
                    return start ? d.HoldIfRunning() : d.ResumeIfHeld();
                });
                if (generation == _generation) {
                    if (start) side.AutoHeld = sent || side.AutoHeld;
                    else side.AutoHeld = false;
                }
                side.Device.PollNow();
            } catch (Exception ex) {
                CommandFailed?.Invoke((start ? "Couldn't hold " : "Couldn't resume ") + (side == _ir ? "the IR program: " : "the bed program: ") + ex.Message);
            } finally {
                side.Pending = false;
                Changed?.Invoke();
            }
        }

        private void OnLinkChanged(DeviceService device) {
            bool isBed = device == _bed.Device;
            var st = device.State;
            if (st == LinkStatus.Connected) {
                if (isBed) _bedLostReported = false;
                else _irLostReported = false;
                return;
            }
            if (st != LinkStatus.NoResponse || !Enabled || !_wasPairRun) return;
            if (isBed && !_bedLostReported) {
                _bedLostReported = true;
                Halt(); // never resume anything after this; the IR gets stopped by the handler
                BedLost?.Invoke();
            } else if (!isBed && !_irLostReported) {
                _irLostReported = true;
                // The IR keeps running on its own; let the bed carry on too rather than leaving it held.
                bool bedHeld = _bed.AutoHeld;
                Halt();
                if (bedHeld && _bed.Device.State == LinkStatus.Connected) {
                    int generation = _generation;
                    _bed.Device.RunAsync(d => generation == _generation && !_userHold && d.ResumeIfHeld())
                        .ContinueWith(t => CommandFailed?.Invoke("Couldn't resume the bed program: " + t.Exception?.GetBaseException().Message),
                            System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                }
                IrLost?.Invoke();
            }
        }
    }
}
