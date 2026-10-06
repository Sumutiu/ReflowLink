// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Linq;

namespace ReflowLink.Model {
    /// <summary>How the controller interprets the ramp-rate parameters r1..r8.</summary>
    public enum RampUnit { PerSecond, PerMinute }

    /// <summary>How the controller interprets the dwell parameters t1..t8.</summary>
    public enum DwellUnit { Seconds, Minutes }

    /// <summary>
    /// Converts between ReflowLink's own units (°C per second, whole seconds) and the units a
    /// particular controller is configured for. Profiles are always edited in °C/s and seconds.
    /// </summary>
    public sealed class ControllerUnits(RampUnit ramp, DwellUnit dwell) {
        public static readonly ControllerUnits SecondsBased = new(RampUnit.PerSecond, DwellUnit.Seconds);
        public static readonly ControllerUnits MinutesBased = new(RampUnit.PerMinute, DwellUnit.Minutes);

        public RampUnit Ramp { get; } = ramp;
        public DwellUnit Dwell { get; } = dwell;

        /// <summary>Decimals sent for a ramp value (per-minute values are 60× larger).</summary>
        public int RampDecimals => Ramp == RampUnit.PerMinute ? 1 : 2;

        /// <summary>Decimals sent for a dwell value (0 for seconds, 2 for minutes).</summary>
        public int DwellDecimals => Dwell == DwellUnit.Minutes ? 2 : 0;

        public double RampToNative(double degreesPerSecond) =>
            Ramp == RampUnit.PerMinute ? degreesPerSecond * 60 : degreesPerSecond;

        public double RampFromNative(double value) =>
            Ramp == RampUnit.PerMinute ? value / 60 : value;

        public double DwellToNative(int seconds) =>
            Dwell == DwellUnit.Minutes ? seconds / 60.0 : seconds;

        public int DwellFromNative(double value) =>
            (int)Math.Round(Dwell == DwellUnit.Minutes ? value * 60 : value, MidpointRounding.AwayFromZero);

        public string RampText => Ramp == RampUnit.PerMinute ? "°C per minute" : "°C per second";
        public string DwellText => Dwell == DwellUnit.Minutes ? "minutes" : "seconds";
        public string Describe() => $"Ramp in {RampText}, hold in {DwellText}";
    }

    /// <summary>A reflow / BGA rework station that uses the PC410 (AL808 protocol) controller.</summary>
    public sealed class StationModel {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Which heater the PC410 controls on this station.</summary>
        public string Controls { get; set; } = "";
        /// <summary>Short notes shown in Settings and the first-run picker.</summary>
        public string Notes { get; set; } = "";
        public double MaxTemperature { get; set; } = 230;
        public ControllerUnits Units { get; set; } = ControllerUnits.SecondsBased;
        /// <summary>True for the generic entry, whose units the user chooses.</summary>
        public bool CustomUnits { get; set; }
    }

    public static class StationCatalog {
        public const string DefaultId = "achi-ir6500";
        public const string GenericId = "generic-pc410";

        public static readonly StationModel[] All = [
            new() {
                Id = "achi-ir6500",
                Name = "ACHI IR6500",
                Controls = "The PC410 controls the 400 W top IR heater. The 800 W bottom preheater has its own controller.",
                Notes = "Ramp in °C/s and hold in seconds (confirmed). ACHI limits the station to 230 °C.",
                MaxTemperature = 230,
            },
            new() {
                Id = "achi-ir6000",
                Name = "ACHI IR6000",
                Controls = "The PC410 controls the upper IR heater. The bottom heater uses a separate CH6 controller.",
                Notes = "Ramp in °C/s and hold in seconds, as in ACHI's manual. Limited to 230 °C like the IR6500.",
                MaxTemperature = 230,
            },
            new() {
                Id = "achi-ir-pro-sc",
                Name = "ACHI IR-PRO-SC",
                Controls = "The PC410 controls the upper heater. The bottom preheat module has its own controller.",
                Notes = "Uses the same units as the other ACHI models (°C/s, seconds); run a short test profile once to confirm. ACHI sets the maximum to 230 °C.",
                MaxTemperature = 230,
            },
            new() {
                Id = GenericId,
                Name = "Other PC410 / AL808 station",
                Controls = "Any station whose controller speaks the ALTEC AL808 / PC900 / PC410 protocol.",
                Notes = "The standard AL808 setting is ramp per minute and hold in minutes; some manufacturers use seconds. Choose the units above and check with a short test profile.",
                MaxTemperature = 250,
                Units = ControllerUnits.MinutesBased,
                CustomUnits = true,
            },
        ];

        public static StationModel Find(string id) =>
            All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

        public static int IndexOf(string id) {
            int i = Array.FindIndex(All, s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? 0 : i;
        }
    }
}
