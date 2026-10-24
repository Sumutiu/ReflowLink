// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReflowLink.Model;

namespace ReflowLink.Protocol {
    /// <summary>A pattern read from both controllers.</summary>
    public sealed class PairRead {
        public ReflowProfile Profile;
        /// <summary>Differences between the two controllers' patterns (step count, ramps, holds).</summary>
        public List<string> Warnings = [];
    }

    /// <summary>
    /// Operations on one pattern number in both the top IR and the bed heat controller. The bed controller runs the same
    /// ramps and holds with its own targets and holdback. When <c>bed</c> is null, only the IR controller is used.
    /// </summary>
    public static class PatternPair {
        public const string IrPrefix = "Top IR: ", BedPrefix = "Bed Heat: ";

        /// <summary>
        /// Writes a profile to pattern <paramref name="slot"/> of both controllers. Both controllers' setpoint limits are
        /// checked before anything is written. If the bed controller refuses part of its profile after the IR pattern
        /// was written, the pair no longer matches, so the pattern counts as changed and gets blocked.
        /// </summary>
        /// <param name="mode">How the IR controller's unused steps are cleared.</param>
        /// <param name="verify">Whether the IR pattern is read back and compared.</param>
        /// <param name="bedMode">How the bed controller's unused steps are cleared (default: same as the IR).</param>
        /// <param name="bedVerify">Whether the bed pattern is read back and compared (default: same as the IR).</param>
        public static UploadResult Write(Pc410Device ir, Pc410Device bed, ReflowProfile profile, int slot, UnusedStepMode mode, bool verify,
            IProgress<OperationProgress> progress, CancellationToken ct, UnusedStepMode? bedMode = null, bool? bedVerify = null) {
            if (bed == null) return ir.UploadProfile(profile, slot, mode, verify, progress, ct);

            // Both must be idle before anything is written, so a busy bed can't leave the pair half-written.
            ir.EnsureIdle("write a profile to the Top IR controller");
            bed.EnsureIdle("write a profile to the Bed Heat controller");
            var bedProfile = profile.ForBed();
            var issues = Pc410Device.CheckTargets(profile, ir.ReadSetpointLimits()).Select(i => IrPrefix + i)
                .Concat(Pc410Device.CheckTargets(bedProfile, bed.ReadSetpointLimits()).Select(i => BedPrefix + i)).ToList();
            if (issues.Count > 0) throw new ProfileRejectedException(slot, issues, false, false);

            UploadResult irResult;
            try {
                irResult = ir.UploadProfile(profile, slot, mode, verify, Half(progress, 0, "Top IR"), ct);
            } catch (ProfileRejectedException ex) {
                throw new ProfileRejectedException(slot, [.. ex.Issues.Select(i => IrPrefix + i)], ex.PatternChanged, ex.PatternCleared, ex.Interrupted);
            }
            try {
                var bedResult = bed.UploadProfile(bedProfile, slot, bedMode ?? mode, bedVerify ?? verify, Half(progress, 1, "Bed Heat"), ct);
                var result = new UploadResult();
                result.Warnings.AddRange(irResult.Warnings.Select(w => IrPrefix + w));
                result.Warnings.AddRange(bedResult.Warnings.Select(w => BedPrefix + w));
                return result;
            } catch (ProfileRejectedException ex) {
                // The IR pattern already holds the new profile: the pair is inconsistent either way.
                throw new ProfileRejectedException(slot, [.. ex.Issues.Select(i => BedPrefix + i)], true, ex.PatternCleared, ex.Interrupted);
            } catch (Exception ex) {
                throw new ProfileRejectedException(slot, [BedPrefix + "writing failed: " + ex.Message], true, false, true);
            }
        }

        /// <summary>Reads pattern <paramref name="slot"/> from both controllers and combines them.</summary>
        public static PairRead Read(Pc410Device ir, Pc410Device bed, int slot, IProgress<OperationProgress> progress, CancellationToken ct) {
            var irProfile = ir.DownloadProfile(slot, bed == null ? progress : Half(progress, 0, "Top IR"), ct);
            var read = new PairRead();
            if (bed == null) {
                read.Profile = irProfile;
                return read;
            }
            var bedProfile = bed.DownloadProfile(slot, Half(progress, 1, "Bed Heat"), ct);
            read.Profile = ReflowProfile.Combine(irProfile, bedProfile, read.Warnings);
            return read;
        }

        /// <summary>
        /// Reads both patterns back and checks them against each controller's limits before a start. Patterns that don't
        /// have the same steps, ramps and holds are refused, because the two heaters would run different programs.
        /// </summary>
        public static ReflowProfile Check(Pc410Device ir, Pc410Device bed, int slot, double irLimit, double bedLimit,
            IProgress<OperationProgress> progress, CancellationToken ct) {
            if (bed == null) return ir.CheckPattern(slot, irLimit, progress, ct);
            ReflowProfile irProfile, bedProfile;
            try {
                irProfile = ir.CheckPattern(slot, irLimit, Half(progress, 0, "Top IR"), ct);
            } catch (ProfileRejectedException ex) {
                throw new ProfileRejectedException(slot, [.. ex.Issues.Select(i => IrPrefix + i)], false, false);
            }
            try {
                bedProfile = bed.CheckPattern(slot, bedLimit, Half(progress, 1, "Bed Heat"), ct);
            } catch (ProfileRejectedException ex) {
                throw new ProfileRejectedException(slot, [.. ex.Issues.Select(i => BedPrefix + i)], false, false);
            }
            var mismatch = new List<string>();
            var combined = ReflowProfile.Combine(irProfile, bedProfile, mismatch);
            if (mismatch.Count > 0) {
                mismatch.Add("Write the profile to this pattern again with bed heat control on.");
                throw new ProfileRejectedException(slot, mismatch, false, false);
            }
            return combined;
        }

        /// <summary>Starts pattern <paramref name="slot"/> on both. If the bed doesn't start, the IR is stopped again.</summary>
        public static void Start(Pc410Device ir, Pc410Device bed, int slot) {
            if (bed != null && ir.ReadAutoTune() == true)
                throw new Pc410Exception("The Top IR controller is auto-tuning. Wait for it to finish, or stop it on the Top IR Control Tuning page.");
            if (bed?.ReadAutoTune() == true)
                throw new Pc410Exception("The Bed Heat controller is auto-tuning. Wait for it to finish, or stop it on the Bed Heat Control Tuning page.");
            try {
                ir.Start(slot);
            } catch (Exception ex) when (bed != null) {
                // The start may have reached the controller even though the reply was lost.
                throw new Pc410Exception($"The Top IR controller didn't confirm the start ({ex.Message}). {StopAfterFailedStart(ir, null)}");
            }
            if (bed == null) return;
            try {
                bed.Start(slot);
            } catch (Exception ex) {
                throw new Pc410Exception($"The Bed Heat controller didn't confirm the start ({ex.Message}). {StopAfterFailedStart(ir, bed)}");
            }
        }

        /// <summary>Stops whatever may have started, then says what's really running.</summary>
        private static string StopAfterFailedStart(Pc410Device ir, Pc410Device bed) {
            var still = new List<string>();
            foreach (var (device, name) in new[] { (bed, "Bed Heat"), (ir, "Top IR") }) {
                if (device == null) continue;
                try { device.Stop(); } catch (Pc410Exception) { }
                try {
                    if (device.ReadState() != ProgramState.Idle) still.Add(name);
                } catch (Pc410Exception) {
                    still.Add(name + " (state unknown)");
                }
            }
            return still.Count == 0
                ? "Both controllers were stopped again."
                : $"Couldn't confirm that every program stopped: {string.Join(", ", still)}. Check the station's front panels.";
        }

        public static void Stop(Pc410Device ir, Pc410Device bed) => Both(ir, bed, d => d.Stop(), "stop");

        /// <summary>Holds whichever controllers are running (an idle one is left alone).</summary>
        public static void Hold(Pc410Device ir, Pc410Device bed) => Both(ir, bed, d => d.HoldIfRunning(), "hold");

        /// <summary>Resumes whichever controllers are on hold. Never sends Run to an idle controller, which would restart it.</summary>
        public static void Resume(Pc410Device ir, Pc410Device bed) => Both(ir, bed, d => d.ResumeIfHeld(), "resume");

        /// <summary>Sends a command to both controllers; both get it even if the first one fails.</summary>
        private static void Both(Pc410Device ir, Pc410Device bed, Action<Pc410Device> command, string verb) {
            var errors = new List<string>();
            foreach (var (device, name) in new[] { (ir, "Top IR"), (bed, "Bed Heat") }) {
                if (device == null) continue;
                try { command(device); } catch (Pc410Exception ex) { errors.Add($"{name}: {ex.Message}"); }
            }
            if (errors.Count > 0) throw new Pc410Exception($"Couldn't {verb} every controller. " + string.Join(" ", errors));
        }

        /// <summary>Maps the progress of one controller's operation onto half of the overall progress.</summary>
        private static IProgress<OperationProgress> Half(IProgress<OperationProgress> progress, int half, string name) =>
            progress == null ? null : new HalfProgress(progress, half, name);

        private sealed class HalfProgress(IProgress<OperationProgress> inner, int half, string name) : IProgress<OperationProgress> {
            public void Report(OperationProgress p) {
                int total = Math.Max(1, p.Total);
                inner.Report(new OperationProgress(half * total + p.Done, total * 2, string.IsNullOrEmpty(p.Text) ? p.Text : $"{name}: {p.Text}"));
            }
        }
    }
}
