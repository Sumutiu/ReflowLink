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
        /// <summary>Ramp rate in °C per second.</summary>
        [XmlAttribute("ramp")] public double RampRate { get; set; } = 1.0;
        /// <summary>Target (level) temperature in °C.</summary>
        [XmlAttribute("target")] public double Target { get; set; } = 150;
        /// <summary>Dwell (hold) time at target in seconds.</summary>
        [XmlAttribute("hold")] public int HoldSeconds { get; set; } = 30;

        public ProfileStep() { }
        public ProfileStep(double ramp, double target, int hold) { RampRate = ramp; Target = target; HoldSeconds = hold; }
        public ProfileStep Clone() => new(RampRate, Target, HoldSeconds);
    }

    [XmlRoot("ReflowProfile")]
    public sealed class ReflowProfile {
        public const int MaxSteps = 8;

        [XmlAttribute("version")] public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = "New profile";
        public string Notes { get; set; } = "";
        /// <summary>Optional programmer holdback band (°C). Null = leave the controller value unchanged.</summary>
        public double? Holdback { get; set; }
        [XmlArray("Steps"), XmlArrayItem("Step")]
        public List<ProfileStep> Steps { get; set; } = [];

        public ReflowProfile Clone() => new() {
            FormatVersion = FormatVersion,
            Name = Name,
            Notes = Notes,
            Holdback = Holdback,
            Steps = [.. Steps.Select(s => s.Clone())],
        };

        public IEnumerable<string> Validate(double maxTemperature) {
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
            if (Holdback.HasValue && (Holdback < 0 || Holdback > 100)) yield return "Holdback must be between 0 and 100 °C.";
        }

        // ---------------------------------------------------------------- file I/O

        public const string FileExtension = ".rlprofile";
        /// <summary>Open filter; also accepts profiles saved by IR6500 Studio (.ir6profile, same format).</summary>
        public const string FileFilter = "ReflowLink profile (*.rlprofile;*.ir6profile)|*.rlprofile;*.ir6profile|XML file (*.xml)|*.xml|All files (*.*)|*.*";
        public const string SaveFilter = "ReflowLink profile (*.rlprofile)|*.rlprofile|XML file (*.xml)|*.xml|All files (*.*)|*.*";

        private static readonly XmlSerializer Serializer = new(typeof(ReflowProfile));

        public void Save(string path) {
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
            string tmp = path + ".tmp";
            using (var w = XmlWriter.Create(tmp, settings)) Serializer.Serialize(w, this);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static ReflowProfile Load(string path) {
            using var r = XmlReader.Create(path);
            var p = (ReflowProfile)Serializer.Deserialize(r);
            p.Steps ??= [];
            if (p.Steps.Count > MaxSteps) p.Steps = [.. p.Steps.Take(MaxSteps)];
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
                        new ProfileStep(1.0, 130, 30),
                        new ProfileStep(0.6, 180, 60),
                        new ProfileStep(0.8, 220, 20),
                        new ProfileStep(1.0, 230, 25),
                    },
                }
            },
            new() {
                Title = "Leaded solder (Sn63/Pb37) — starting point",
                Create = () => new ReflowProfile {
                    Name = "Leaded Sn63Pb37",
                    Notes = "Example profile. Tune ramp rates and hold times for your board and sensor placement.",
                    Steps = {
                        new ProfileStep(1.0, 140, 30),
                        new ProfileStep(0.6, 160, 60),
                        new ProfileStep(0.8, 190, 20),
                        new ProfileStep(1.0, 210, 25),
                    },
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
