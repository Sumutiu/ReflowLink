// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace ReflowLink.Model {
    public sealed class ProfileStep {
        private double _bedTarget = double.NaN;

        /// <summary>Ramp rate in °C per second (used by both controllers).</summary>
        [XmlAttribute("ramp")] public double RampRate { get; set; } = 1.0;
        /// <summary>Top IR target (level) temperature in °C.</summary>
        [XmlAttribute("target")] public double Target { get; set; } = 150;
        /// <summary>Bed heat target in °C. Profiles saved before bed heat control read it as the IR target.</summary>
        [XmlAttribute("bedTarget")]
        public double BedTarget { get => double.IsNaN(_bedTarget) ? Target : _bedTarget; set => _bedTarget = value; }
        /// <summary>Dwell (hold) time at target in seconds (used by both controllers).</summary>
        [XmlAttribute("hold")] public int HoldSeconds { get; set; } = 30;
        /// <summary>During this step the IR is held while the bed is behind.</summary>
        [XmlAttribute("waitForBed")] public bool WaitForBed { get; set; }
        /// <summary>During this step the bed is held while the IR is behind.</summary>
        [XmlAttribute("waitForIr")] public bool WaitForIr { get; set; }

        public ProfileStep() { }
        public ProfileStep(double ramp, double target, int hold) { RampRate = ramp; Target = target; HoldSeconds = hold; }

        public ProfileStep Clone() => new(RampRate, Target, HoldSeconds) {
            BedTarget = BedTarget,
            WaitForBed = WaitForBed,
            WaitForIr = WaitForIr,
        };
    }

    [XmlRoot("ReflowProfile")]
    public sealed class ReflowProfile {
        public const int MaxSteps = 8;

        [XmlAttribute("version")] public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = "New profile";
        public string Notes { get; set; } = "";
        /// <summary>Holdback range the PC410 accepts (it refuses 0, so holdback can't be switched off).</summary>
        public const double HoldbackMin = 1, HoldbackMax = 100, HoldbackDefault = 5;
        public static readonly string HoldbackRangeMessage = $"Holdback must be between {HoldbackMin:0} and {HoldbackMax:0} °C.";

        /// <summary>
        /// Holdback band (°C). The PC410 has one holdback for all patterns, so writing any profile sets it for every
        /// pattern. Null only in files saved by older versions; the editor then uses <see cref="HoldbackDefault"/>.
        /// </summary>
        public double? Holdback { get; set; }
        /// <summary>Holdback band (°C) of the bed heat controller; also used to decide when the bed is behind.</summary>
        public double? BedHoldback { get; set; }
        [XmlArray("Steps"), XmlArrayItem("Step")]
        public List<ProfileStep> Steps { get; set; } = [];

        /// <summary>Set by <see cref="Load"/> when the file needed changes to fit (too many steps, no steps). Not saved.</summary>
        [XmlIgnore]
        public string LoadNote { get; private set; }

        public ReflowProfile Clone() => new() {
            FormatVersion = FormatVersion,
            Name = Name,
            Notes = Notes,
            Holdback = Holdback,
            BedHoldback = BedHoldback,
            Steps = [.. Steps.Select(s => s.Clone())],
        };

        /// <summary>The profile as the bed heat controller runs it: same ramps and holds, bed targets and holdback.</summary>
        public ReflowProfile ForBed() => new() {
            Name = Name,
            Notes = Notes,
            Holdback = BedHoldback ?? HoldbackDefault,
            Steps = [.. Steps.Select(s => new ProfileStep(s.RampRate, s.BedTarget, s.HoldSeconds))],
        };

        /// <summary>
        /// Combines a pattern read from the IR controller with the same pattern read from the bed controller.
        /// Differences in step count, ramps or holds are reported, because both controllers should run the same timing.
        /// </summary>
        public static ReflowProfile Combine(ReflowProfile ir, ReflowProfile bed, List<string> warnings) {
            var p = ir.Clone();
            p.BedHoldback = bed?.Holdback;
            if (bed == null) return p;
            if (bed.Steps.Count != ir.Steps.Count)
                warnings?.Add($"The bed controller has {bed.Steps.Count} step(s) in this pattern and the IR controller {ir.Steps.Count}.");
            for (int i = 0; i < p.Steps.Count; i++) {
                if (i >= bed.Steps.Count) continue;
                var b = bed.Steps[i];
                p.Steps[i].BedTarget = b.Target;
                if (Math.Abs(b.RampRate - ir.Steps[i].RampRate) > 0.011 || b.HoldSeconds != ir.Steps[i].HoldSeconds)
                    warnings?.Add($"Step {i + 1}: the bed controller has a different ramp or hold time than the IR controller.");
            }
            return p;
        }

        /// <summary>Checks the profile. Pass <paramref name="bedMaxTemperature"/> when bed heat control is used.</summary>
        public IEnumerable<string> Validate(double maxTemperature, double? bedMaxTemperature = null) {
            if (Steps.Count == 0) yield return "The profile needs at least one step.";
            if (Steps.Count > MaxSteps) yield return $"The controller supports at most {MaxSteps} steps.";
            for (int i = 0; i < Steps.Count; i++) {
                var s = Steps[i];
                int n = i + 1;
                if (!(s.RampRate > 0)) yield return $"Step {n}: ramp rate must be greater than 0 °C/s.";
                if (s.RampRate > 10) yield return $"Step {n}: ramp rate above 10 °C/s is not realistic for an IR heater.";
                if (s.Target < 0) yield return $"Step {n}: target cannot be negative.";
                if (s.Target > maxTemperature) yield return $"Step {n}: target {s.Target:0.#} °C exceeds the {maxTemperature:0} °C limit.";
                if (s.HoldSeconds < 0) yield return $"Step {n}: hold time cannot be negative.";
                if (s.HoldSeconds > 9999) yield return $"Step {n}: hold time must be 9999 s or less.";
            }
            if (Holdback.HasValue && (Holdback < HoldbackMin || Holdback > HoldbackMax)) yield return HoldbackRangeMessage;
            if (bedMaxTemperature is double bedMax) {
                for (int i = 0; i < Steps.Count; i++) {
                    var s = Steps[i];
                    if (s.BedTarget < 0) yield return $"Step {i + 1}: bed target cannot be negative.";
                    if (s.BedTarget > bedMax) yield return $"Step {i + 1}: bed target {s.BedTarget:0.#} °C exceeds the {bedMax:0} °C bed limit.";
                }
                if (BedHoldback.HasValue && (BedHoldback < HoldbackMin || BedHoldback > HoldbackMax))
                    yield return $"Bed holdback must be between {HoldbackMin:0} and {HoldbackMax:0} °C.";
            }
        }

        // ---------------------------------------------------------------- file I/O

        public const string FileExtension = ".rlprofile";
        /// <summary>Open filter; also accepts profiles saved by IR6500 Studio (.ir6profile, same format).</summary>
        public const string FileFilter = "ReflowLink profile (*.rlprofile;*.ir6profile)|*.rlprofile;*.ir6profile|XML file (*.xml)|*.xml|All files (*.*)|*.*";
        public const string SaveFilter = "ReflowLink profile (*.rlprofile)|*.rlprofile|XML file (*.xml)|*.xml|All files (*.*)|*.*";

        private static readonly XmlSerializer Serializer = new(typeof(ReflowProfile));

        /// <summary>Saves the profile; the old file is only replaced once the new one is completely written.</summary>
        public void Save(string path) {
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
            SafeFile.Write(path, stream => {
                using var w = XmlWriter.Create(stream, settings);
                Serializer.Serialize(w, this);
            });
        }

        public static ReflowProfile Load(string path) {
            using var r = XmlReader.Create(path);
            var p = (ReflowProfile)Serializer.Deserialize(r);
            p.Steps = [.. (p.Steps ?? []).Where(s => s != null)];
            if (p.Steps.Count > MaxSteps) {
                p.LoadNote = $"The file has {p.Steps.Count} steps, but the PC410 runs at most {MaxSteps}: only the first {MaxSteps} were loaded.";
                p.Steps = [.. p.Steps.Take(MaxSteps)];
            } else if (p.Steps.Count == 0) {
                p.LoadNote = "The file has no steps: the editor starts with one default step.";
            }
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(path);
            return p;
        }
    }

    public struct CurvePoint(double t, double v) {
        public double Time = t;
        public double Temp = v;
    }

    public sealed class PlannedCurve {
        public List<CurvePoint> Points = [];
        /// <summary>Start time (s) of each step's ramp, for markers.</summary>
        public List<double> StepStarts = [];
        public double TotalSeconds;
        public double PeakTemp;
    }

    public static class ProfileMath {
        /// <summary>Ramps steeper than this (°C/s) risk cracking components and are flagged in the editor.</summary>
        public const double SteepRamp = 3;

        /// <summary>Seconds the curve spends above <paramref name="temperature"/> (piecewise linear between its points).</summary>
        public static double TimeAbove(PlannedCurve curve, double temperature) {
            double total = 0;
            for (int i = 1; i < curve.Points.Count; i++) {
                var a = curve.Points[i - 1];
                var b = curve.Points[i];
                double dt = b.Time - a.Time;
                if (dt <= 0) continue;
                bool aAbove = a.Temp > temperature, bAbove = b.Temp > temperature;
                if (aAbove && bAbove) total += dt;
                else if (aAbove != bAbove) {
                    // Crosses the line: count the part above it.
                    double f = (temperature - a.Temp) / (b.Temp - a.Temp);
                    total += aAbove ? f * dt : (1 - f) * dt;
                }
            }
            return total;
        }

        /// <summary>Lists how pattern <paramref name="b"/> differs from the editor's profile <paramref name="a"/> (bed targets when <paramref name="bed"/>).</summary>
        public static List<string> Differences(ReflowProfile a, ReflowProfile b, bool bed) {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var list = new List<string>();
            if (a.Steps.Count != b.Steps.Count) list.Add($"Steps: editor {a.Steps.Count}, pattern {b.Steps.Count}.");
            for (int i = 0; i < Math.Min(a.Steps.Count, b.Steps.Count); i++) {
                var x = a.Steps[i];
                var y = b.Steps[i];
                if (Math.Abs(x.RampRate - y.RampRate) > 0.011) list.Add($"Step {i + 1} ramp: editor {x.RampRate.ToString("0.##", c)}, pattern {y.RampRate.ToString("0.##", c)} °C/s.");
                if (Math.Abs(x.Target - y.Target) > 0.11) list.Add($"Step {i + 1} target: editor {x.Target.ToString("0.#", c)}, pattern {y.Target.ToString("0.#", c)} °C.");
                if (x.HoldSeconds != y.HoldSeconds) list.Add($"Step {i + 1} hold: editor {x.HoldSeconds} s, pattern {y.HoldSeconds} s.");
                if (bed && Math.Abs(x.BedTarget - y.BedTarget) > 0.11) list.Add($"Step {i + 1} bed target: editor {x.BedTarget.ToString("0.#", c)}, pattern {y.BedTarget.ToString("0.#", c)} °C.");
            }
            if (a.Holdback.HasValue && b.Holdback.HasValue && Math.Abs(a.Holdback.Value - b.Holdback.Value) > 0.05)
                list.Add($"Holdback: editor {a.Holdback.Value.ToString("0.#", c)}, controller {b.Holdback.Value.ToString("0.#", c)} °C.");
            return list;
        }

        /// <summary>The steepest ramp rate of the profile (°C/s); 0 without steps.</summary>
        public static double SteepestRamp(ReflowProfile profile) =>
            profile.Steps.Count == 0 ? 0 : profile.Steps.Max(s => s.RampRate);

        /// <summary>Ideal setpoint trajectory of a profile when started from <paramref name="startTemp"/>.</summary>
        public static PlannedCurve BuildPlannedCurve(ReflowProfile profile, double startTemp) {
            var c = new PlannedCurve();
            double t = 0, temp = startTemp;
            c.Points.Add(new CurvePoint(0, temp));
            c.PeakTemp = temp;
            foreach (var s in profile.Steps) {
                c.StepStarts.Add(t);
                double rate = s.RampRate > 0 ? s.RampRate : 1e9;
                double ramp = Math.Abs(s.Target - temp) / rate;
                t += ramp;
                temp = s.Target;
                c.Points.Add(new CurvePoint(t, temp));
                if (s.HoldSeconds > 0) {
                    t += s.HoldSeconds;
                    c.Points.Add(new CurvePoint(t, temp));
                }
                c.PeakTemp = Math.Max(c.PeakTemp, temp);
            }
            c.TotalSeconds = t;
            return c;
        }

        /// <summary>
        /// Bed setpoint path with a single PC410: the bed has no program, it follows the IR program and reaches each step's
        /// bed target when the IR reaches its target (see <c>BedPlan</c>).
        /// </summary>
        public static PlannedCurve BuildFollowingCurve(ReflowProfile profile, double irStart, double bedStart) {
            var c = new PlannedCurve();
            double t = 0, temp = irStart;
            c.Points.Add(new CurvePoint(0, bedStart));
            c.PeakTemp = bedStart;
            foreach (var s in profile.Steps) {
                c.StepStarts.Add(t);
                double rate = s.RampRate > 0 ? s.RampRate : 1e9;
                t += Math.Abs(s.Target - temp) / rate;
                temp = s.Target;
                c.Points.Add(new CurvePoint(t, s.BedTarget));
                if (s.HoldSeconds > 0) {
                    t += s.HoldSeconds;
                    c.Points.Add(new CurvePoint(t, s.BedTarget));
                }
                c.PeakTemp = Math.Max(c.PeakTemp, s.BedTarget);
            }
            c.TotalSeconds = t;
            return c;
        }

        /// <summary>The bed's planned path: its own program on a second PC410, or following the IR with a single PC410.</summary>
        public static PlannedCurve BuildBedCurve(ReflowProfile profile, double irStart, double bedStart, bool followsIr) =>
            followsIr ? BuildFollowingCurve(profile, irStart, bedStart) : BuildPlannedCurve(profile.ForBed(), bedStart);

        public static string FormatDuration(double seconds) {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(Math.Round(seconds));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        }
    }

    public static class ProfilePresets {
        public sealed class Preset {
            public string Title;
            public Func<ReflowProfile> Create;
        }

        /// <summary>
        /// Starting points only — every board, sensor position and heater behaves differently.
        /// Temperatures are as measured by the station's board thermocouple.
        /// </summary>
        public static readonly Preset[] All = [
            new() {
                Title = "Lead-free solder (SAC305) — starting point",
                Create = () => new ReflowProfile {
                    Name = "Lead-free SAC305",
                    Notes = "Example profile. The board sensor usually reads below the joint temperature; verify with an external thermocouple.",
                    Steps = {
                        new ProfileStep(1.0, 160, 40),
                        new ProfileStep(0.6, 170, 40),
                        new ProfileStep(0.8, 180, 50),
                        new ProfileStep(0.8, 200, 30),
                        new ProfileStep(0.6, 230, 100),
                        new ProfileStep(1.0, 200, 0),
                        new ProfileStep(2.0, 160, 0),
                        new ProfileStep(4.0, 100, 0),
                    },
                    Holdback = 5,
                    BedHoldback = 5,
                }
            },
            new() {
                Title = "Leaded solder (Sn63/Pb37) — starting point",
                Create = () => new ReflowProfile {
                    Name = "Leaded Sn63Pb37",
                    Notes = "Example profile. Tune ramp rates and hold times for your board and sensor placement.",
                    Steps = {
                        new ProfileStep(1.0, 120, 30),
                        new ProfileStep(0.6, 150, 40),
                        new ProfileStep(0.6, 170, 30),
                        new ProfileStep(0.8, 210, 20),
                        new ProfileStep(0.6, 220, 30),
                        new ProfileStep(1.0, 150, 0),
                        new ProfileStep(2.0, 100, 0),
                    },
                    Holdback = 5,
                    BedHoldback = 5,
                }
            },
            new() {
                Title = "Board pre-bake / gentle warm-up",
                Create = () => new ReflowProfile {
                    Name = "Pre-heat 120 °C",
                    Notes = "Slow warm-up and soak, useful to drive moisture out before rework.",
                    Steps = {
                        new ProfileStep(0.5, 120, 600),
                    },
                }
            },
            new() {
                Title = "Empty profile (1 step)",
                Create = () => new ReflowProfile {
                    Name = "New profile",
                    Steps = { new ProfileStep(1.0, 150, 30) },
                }
            },
        ];
    }
}
