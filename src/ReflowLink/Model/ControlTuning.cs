// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReflowLink.Model {
    public enum TuningGroup { Pid, Output, Advanced }

    /// <summary>One controller setting shown on the Control Tuning page.</summary>
    public sealed class TuningParameter {
        /// <summary>Two-character communication code (case matters: HB is cutback, Hb is holdback).</summary>
        public string Mnemonic { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Name on the controller's display, or "" when ACHI hides the setting there.</summary>
        public string FrontPanel { get; set; } = "";
        public string Unit { get; set; } = "";
        public double Min { get; set; }
        public double Max { get; set; }
        public int Decimals { get; set; }
        /// <summary>Value read from a factory ACHI IR6500; used while no controller is connected.</summary>
        public double Factory { get; set; }
        public TuningGroup Group { get; set; }
        public string What { get; set; } = "";
        public string Higher { get; set; } = "";
        public string Lower { get; set; } = "";
        public string Caution { get; set; } = "";

        public string Format(double value) {
            string n = value.ToString("F" + Decimals, CultureInfo.InvariantCulture);
            return Unit.Length == 0 ? n : Unit == "%" ? n + " %" : $"{n} {Unit}";
        }

        public string RangeText => $"{Name} must be between {Format(Min)} and {Format(Max)}.";

        public bool InRange(double value) => value >= Min - 1e-9 && value <= Max + 1e-9;

        /// <summary>Largest difference between written and read-back values that still counts as the same.</summary>
        public double Tolerance => 0.5 * Math.Pow(10, -Decimals) + 1e-6;

        public bool Same(double a, double b) => Math.Abs(a - b) <= Tolerance;

        /// <summary>Rounds to the resolution the value is written with, so what's shown, written and compared agree.</summary>
        public double Round(double value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>The PC410 settings ReflowLink can read and change, with plain-language explanations.</summary>
    public static class TuningCatalog {
        public const string AutoTune = "XS";

        public static readonly TuningParameter Proportional = new() {
            Mnemonic = "XP",
            Name = "Proportional band (P)",
            FrontPanel = "P",
            Unit = "°C",
            Min = 1,
            Max = 999,
            Decimals = 0, // the controller reports whole degrees ("XP  10.")
            Factory = 10,
            Group = TuningGroup.Pid,
            What = "How hard the heater pushes against the gap between the temperature and the setpoint. It's a temperature band: "
                 + "with P = 10 °C the heater gets full power when the board is 10 °C below the setpoint, half power at 5 °C below, and less as the gap closes.",
            Lower = "Pushes harder: follows ramps more closely, but the temperature swings around the target and can keep oscillating.",
            Higher = "Gentler: calmer, with fewer swings, but the temperature lags behind ramps and takes longer to settle.",
            Caution = "Change it in small steps, for example from 10 to 8 or 12, and check the result with a real run.",
        };

        public static readonly TuningParameter Integral = new() {
            Mnemonic = "TI",
            Name = "Integral time (I)",
            FrontPanel = "I",
            Unit = "s",
            Min = 1,
            Max = 8000,
            Decimals = 0,
            Factory = 61,
            Group = TuningGroup.Pid,
            What = "Removes the gap that P alone leaves behind, by adding power for as long as the temperature stays below the setpoint "
                 + "(and removing it while it's above). It's a time: the shorter it is, the faster and stronger it acts.",
            Lower = "Shorter: reaches the setpoint sooner after a ramp, but builds up too much, overshoots and can cause slow swings.",
            Higher = "Longer: calmer, but the temperature can sit a few degrees off the setpoint for a long time during holds.",
        };

        public static readonly TuningParameter Derivative = new() {
            Mnemonic = "TD",
            Name = "Derivative time (D)",
            FrontPanel = "d",
            Unit = "s",
            Min = 1,
            Max = 999,
            Decimals = 0,
            Factory = 9,
            Group = TuningGroup.Pid,
            What = "Brakes the heater when the temperature closes in on the setpoint quickly, so it doesn't shoot past it. "
                 + "It's a time: the longer it is, the harder it brakes.",
            Lower = "Shorter: less braking, so more overshoot at the end of each ramp.",
            Higher = "Longer: less overshoot, but too much makes the output nervous, slows settling and amplifies thermocouple noise.",
        };

        public static readonly TuningParameter OutputLimit = new() {
            Mnemonic = "HO",
            Name = "Output power limit",
            FrontPanel = "H PL",
            Unit = "%",
            Min = 0,
            Max = 100,
            Decimals = 1,
            Factory = 100,
            Group = TuningGroup.Output,
            What = "The most power the controller will ever give the heater. 100 % is the heater's full power.",
            Lower = "Gentler heating for small or sensitive boards, but fast ramps can't be followed (holdback may pause the program) "
                  + "and high targets may never be reached.",
            Higher = "More heating power available, up to the heater's full power at 100 %.",
            Caution = "Set it too low and the station can't reach reflow temperatures. The graph includes this limit.",
        };

        public static readonly TuningParameter CycleTime = new() {
            Mnemonic = "CH",
            Name = "Heat cycle time",
            FrontPanel = "H c.t",
            Unit = "s",
            Min = 0.3,
            Max = 80,
            Decimals = 1,
            Factory = 3,
            Group = TuningGroup.Output,
            What = "The heater is switched fully on and off, and the power is set by how long it's on in each cycle. "
                 + "With 3.0 s and 40 % power, it's on for 1.2 s and off for 1.8 s.",
            Lower = "Shorter: smoother temperature, but more switching. Fine for a solid-state relay (SSR); it quickly wears out a mechanical relay or contactor.",
            Higher = "Longer: fewer switches, but the temperature ripples with every cycle.",
            Caution = "Don't go below the factory value unless you know the heater is switched by a solid-state relay.",
        };

        public static readonly TuningParameter SensorBreakPower = new() {
            Mnemonic = "BP",
            Name = "Sensor break power",
            FrontPanel = "SnbP",
            Unit = "%",
            Min = 0,
            Max = 100,
            Decimals = 1,
            Factory = 0,
            Group = TuningGroup.Output,
            What = "The power the heater gets if the thermocouple breaks or comes loose and the controller loses the temperature reading.",
            Lower = "0 % switches the heater off when the reading is lost: the safe setting.",
            Higher = "Keeps heating with no temperature reading at all.",
            Caution = "Leave this at 0 %. Anything higher can overheat the board or the station, with nothing measuring the temperature.",
        };

        public static readonly TuningParameter HighCutback = new() {
            Mnemonic = "HB",
            Name = "High cutback",
            Unit = "°C",
            Min = 0,
            Max = 999,
            Decimals = 0,
            Factory = 0,
            Group = TuningGroup.Advanced,
            What = "A band above the setpoint. While the temperature is above it, the controller ignores I and keeps the heater off, which limits overshoot. "
                 + "On Eurotherm-style controllers 0 means automatic (3 × P); that isn't confirmed for this controller.",
            Lower = "Smaller band: the controller keeps the heater off until the temperature is closer to the setpoint.",
            Higher = "Larger band: normal PID control takes over further above the setpoint.",
        };

        public static readonly TuningParameter LowCutback = new() {
            Mnemonic = "LB",
            Name = "Low cutback",
            Unit = "°C",
            Min = 0,
            Max = 999,
            Decimals = 0,
            Factory = 0,
            Group = TuningGroup.Advanced,
            What = "A band below the setpoint. While the temperature is further below than this, the heater runs at full power and I is paused, "
                 + "so it can't build up and cause overshoot. 0 normally means automatic (3 × P).",
            Lower = "Smaller band: full power for longer, so a faster approach but more overshoot.",
            Higher = "Larger band: PID takes over earlier, so a gentler approach with less overshoot, but slower.",
        };

        public static readonly TuningParameter HighAlarm = new() {
            Mnemonic = "HA",
            Name = "High alarm",
            Unit = "°C",
            Min = 0,
            Max = 999,
            Decimals = 0,
            Factory = 300,
            Group = TuningGroup.Advanced,
            What = "Temperature above which the controller switches on its alarm output (AL1).",
            Lower = "The alarm triggers at a lower temperature.",
            Higher = "The alarm triggers at a higher temperature, or never if it's above anything the station reaches.",
            Caution = "What AL1 is wired to on your station isn't known. Check the wiring before changing this.",
        };

        public static readonly TuningParameter LowAlarm = new() {
            Mnemonic = "LA",
            Name = "Low alarm",
            Unit = "°C",
            Min = 0,
            Max = 999,
            Decimals = 0,
            Factory = 500,
            Group = TuningGroup.Advanced,
            What = "Temperature below which the alarm output switches on. The factory value (500 °C) is above the high alarm, "
                 + "which suggests this alarm is switched off in the controller's configuration.",
            Lower = "The alarm triggers only at lower temperatures.",
            Higher = "The alarm triggers at higher temperatures.",
            Caution = "If this alarm is active, a value above room temperature keeps the alarm on all the time.",
        };

        public static readonly TuningParameter DeviationAlarm = new() {
            Mnemonic = "DA",
            Name = "Deviation alarm",
            Unit = "°C",
            Min = 0,
            Max = 99.99,
            Decimals = 2,
            Factory = 1,
            Group = TuningGroup.Advanced,
            What = "Switches the alarm output on when the temperature is further than this from the setpoint. "
                 + "At the factory value (1 °C) it would trigger almost constantly, so it's most likely switched off in the configuration.",
            Lower = "The alarm triggers at smaller differences.",
            Higher = "The alarm only triggers when the temperature is far off the setpoint.",
        };

        public static readonly TuningParameter RepeatCount = new() {
            Mnemonic = "Lc",
            Name = "Program repeat count",
            Unit = "",
            Min = 1,
            Max = 200,
            Decimals = 0,
            Factory = 200,
            Group = TuningGroup.Advanced,
            What = "How many times a program runs when repeating is switched on in the controller's configuration. "
                 + "Programs on ACHI stations run once, so repeating is off and this value is ignored.",
            Lower = "Fewer repeats (only if repeating is switched on).",
            Higher = "More repeats (only if repeating is switched on).",
        };

        public static readonly TuningParameter SetpointRate = new() {
            Mnemonic = "SR",
            Name = "Ramp-to-setpoint rate",
            Unit = "°C/min",
            Min = 0.01,
            Max = 99.99,
            Decimals = 2,
            Factory = 10,
            Group = TuningGroup.Advanced,
            What = "How fast the setpoint moves when it's changed outside a program. ReflowLink's profiles don't use it.",
            Lower = "The setpoint moves more slowly after a manual change.",
            Higher = "The setpoint moves faster after a manual change.",
        };

        public static readonly TuningParameter[] All = [
            Proportional, Integral, Derivative,
            OutputLimit, CycleTime, SensorBreakPower,
            HighCutback, LowCutback, HighAlarm, LowAlarm, DeviationAlarm, RepeatCount, SetpointRate,
        ];

        public static TuningParameter Find(string mnemonic) => All.FirstOrDefault(p => p.Mnemonic == mnemonic);

        public static IEnumerable<TuningParameter> InGroup(TuningGroup group) => All.Where(p => p.Group == group);
    }

    /// <summary>Values of all tuning parameters, either read from the controller or the factory defaults.</summary>
    public sealed class TuningSnapshot {
        /// <summary>Value per mnemonic; null when the controller didn't report it.</summary>
        public Dictionary<string, double?> Values { get; } = [];
        /// <summary>True while auto-tune is running; null when not reported.</summary>
        public bool? AutoTune { get; set; }
        public bool FromController { get; set; }
        public DateTime? ReadAt { get; set; }

        public double? this[TuningParameter p] => Values.TryGetValue(p.Mnemonic, out var v) ? v : null;

        public static TuningSnapshot Factory() {
            var s = new TuningSnapshot { AutoTune = false };
            foreach (var p in TuningCatalog.All) s.Values[p.Mnemonic] = p.Factory;
            return s;
        }
    }

    public readonly struct SimPoint(double time, double setpoint, double temperature, double output) {
        public double Time { get; } = time;
        public double Setpoint { get; } = setpoint;
        public double Temperature { get; } = temperature;
        public double Output { get; } = output;
    }

    public sealed class PidSimulation {
        public List<SimPoint> Points { get; } = [];
        /// <summary>Time the setpoint ramp ends and the hold starts.</summary>
        public double RampEnd { get; set; }
        public double Duration { get; set; }
        /// <summary>Largest amount the temperature trails the setpoint during the ramp.</summary>
        public double RampLag { get; set; }
        /// <summary>Largest amount above the target after the ramp (0 when it never goes above).</summary>
        public double Overshoot { get; set; }
        /// <summary>Seconds after the ramp until it stays within ±1 °C; null if it doesn't within the simulated hold.</summary>
        public double? SettleSeconds { get; set; }
        /// <summary>How often the temperature crosses the target during the hold.</summary>
        public int Swings { get; set; }
    }

    /// <summary>
    /// Simulates a PID loop on a generic IR heater (heater lag, dead time, board heat-up), to show how P, I, D and the
    /// output limit change the response. The model is illustrative, not a measurement of a particular station.
    /// </summary>
    public static class PidSimulator {
        public const double Ambient = 25;
        private const double Gain = 450;      // °C above ambient at 100 % power, at equilibrium
        private const double BoardLag = 140;  // s
        private const double HeaterLag = 8;   // s, IR emitter warm-up
        private const double DeadTime = 3;    // s, heater to thermocouple
        private const double Step = 0.1;      // s

        public static PidSimulation Run(double p, double i, double d, double outputLimit, double rampRate, double target, double holdSeconds = 300) {
            p = Math.Max(0.1, p);
            rampRate = Math.Max(0.05, rampRate);
            target = Math.Max(Ambient + 5, target);
            outputLimit = Math.Max(0, Math.Min(100, outputLimit));
            double rampEnd = (target - Ambient) / rampRate;
            double duration = rampEnd + holdSeconds;
            var sim = new PidSimulation { RampEnd = rampEnd, Duration = duration };

            double pv = Ambient, heater = 0, integral = 0, lastError = 0, dFiltered = 0;
            var delay = new Queue<double>(Enumerable.Repeat(0.0, (int)Math.Round(DeadTime / Step)));
            int steps = (int)Math.Ceiling(duration / Step);
            int every = (int)Math.Round(1 / Step);
            double cutback = 3 * p;
            double lastSide = 0;

            for (int k = 0; k <= steps; k++) {
                double t = k * Step;
                double sp = Math.Min(target, Ambient + rampRate * t);
                double e = sp - pv;

                // Derivative of the error, filtered so it doesn't react to every tiny change.
                double de = k == 0 ? 0 : (e - lastError) / Step;
                lastError = e;
                double filter = Math.Max(Step, d / 8);
                dFiltered += (de - dFiltered) * Step / filter;

                double gain = 100 / p;
                double proportional = gain * (e + d * dFiltered);
                // Integral only inside the cutback band, and only while it doesn't push the output past its limits.
                if (i > 0 && Math.Abs(e) < cutback) {
                    double next = integral + e * Step / i;
                    double trial = proportional + gain * next;
                    if ((trial <= outputLimit || e < 0) && (trial >= 0 || e > 0)) integral = next;
                }
                double output = Math.Max(0, Math.Min(outputLimit, proportional + gain * integral));

                delay.Enqueue(output);
                double applied = delay.Dequeue();
                heater += (applied / 100 - heater) * Step / HeaterLag;
                pv += (Gain * heater - (pv - Ambient)) * Step / BoardLag;

                if (k % every == 0) sim.Points.Add(new SimPoint(t, sp, pv, output));

                if (t < rampEnd) {
                    sim.RampLag = Math.Max(sim.RampLag, sp - pv);
                } else {
                    sim.Overshoot = Math.Max(sim.Overshoot, pv - target);
                    double diff = pv - target;
                    double side = diff > 0.5 ? 1 : diff < -0.5 ? -1 : 0;
                    if (side != 0) {
                        if (lastSide != 0 && side != lastSide) sim.Swings++;
                        lastSide = side;
                    }
                }
            }

            // Settled: within ±1 °C from some point after the ramp until the end.
            double? settledAt = null;
            for (int n = sim.Points.Count - 1; n >= 0; n--) {
                var pt = sim.Points[n];
                if (pt.Time < rampEnd || Math.Abs(pt.Temperature - target) > 1) break;
                settledAt = pt.Time;
            }
            if (settledAt.HasValue && duration - settledAt.Value >= 30) sim.SettleSeconds = Math.Max(0, settledAt.Value - rampEnd);
            return sim;
        }
    }
}
