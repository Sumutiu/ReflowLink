// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ReflowLink.Protocol;

namespace ReflowLink.Model {
    /// <summary>Connection fields shared by the top IR settings and the bed heat settings.</summary>
    public interface IConnectionSettings {
        string PortName { get; set; }
        int BaudRate { get; set; }
        int Address { get; set; }
        int ResponseTimeoutMs { get; set; }
        int Retries { get; set; }
        int PollIntervalMs { get; set; }
        bool DtrEnable { get; set; }
        bool RtsEnable { get; set; }
        bool UseSimulator { get; set; }
        double SimulatorSpeed { get; set; }
    }

    public sealed class AppSettings : IConnectionSettings {
        /// <summary>Settings format: 0 = saved before versions were tracked (default temperature limit 230 / 250 °C).</summary>
        public const int CurrentVersion = 2;

        /// <summary>Format of the loaded file; missing in older files, so it reads as 0.</summary>
        public int SettingsVersion { get; set; }

        // Station
        /// <summary>Selected station from <see cref="StationCatalog"/>. Empty until the user picks one on first run.</summary>
        public string StationId { get; set; } = "";
        public RampUnit GenericRampUnit { get; set; } = RampUnit.PerMinute;
        public DwellUnit GenericDwellUnit { get; set; } = DwellUnit.Minutes;

        // Connection
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; } = 9600;
        public int Address { get; set; } = 1;
        public int ResponseTimeoutMs { get; set; } = 500;
        public int Retries { get; set; } = 2;
        public int PollIntervalMs { get; set; } = 500;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public bool UseSimulator { get; set; } = false;
        public double SimulatorSpeed { get; set; } = 1;
        /// <summary>
        /// Show the Top IR controller's second input (IN2, P2) as "IN2 (Top IR)": information only. Always read with a
        /// single PC410, where IN2 is the bed temperature.
        /// </summary>
        public bool ReadIn2 { get; set; }

        /// <summary>Bottom (bed) heater: off, the top PC410's IN2 + OUT2, or a second PC410.</summary>
        public BedSettings Bed { get; set; } = new();

        /// <summary>
        /// Wait for Bed / Wait for IR choices (and, for a single PC410, the bed targets) per pattern; the controllers can't
        /// store them, so ReflowLink does.
        /// </summary>
        public List<PatternSync> PatternSyncs { get; set; } = [];

        /// <summary>
        /// The controllers were seen counting ramp and dwell as separate segments (a step is two segments). Learned during a
        /// run and kept, so Wait for Bed / Wait for IR and the step display are right from the start of the next run.
        /// </summary>
        public bool SegmentsPaired { get; set; }

        /// <summary>Longest Wait for Bed / Wait for IR hold before ReflowLink asks what to do (minutes; 0 = no limit).</summary>
        public double MaxWaitMinutes { get; set; } = 10;

        /// <summary>P, I and D of the Top IR controller from before its last auto-tune, so they can be put back.</summary>
        public TuningBackup IrTuningBackup { get; set; }

        /// <summary>P, I and D of the second PC410 from before its last auto-tune.</summary>
        public TuningBackup BedTuningBackup { get; set; }

        // Profiles
        public double MaxTemperature { get; set; } = StationModel.DefaultMaxTemperature;
        public UnusedStepMode UnusedSteps { get; set; } = UnusedStepMode.Zeros;
        public bool VerifyAfterWrite { get; set; } = true;
        public double PreviewStartTemperature { get; set; } = 25;
        public string LastProfilePath { get; set; } = "";
        /// <summary>Controller patterns that were left with rejected or partial settings; they can't be started until rewritten.</summary>
        public List<int> BlockedPatterns { get; set; } = [];
        /// <summary>
        /// Pattern being written right now (-1 = none), saved before the write starts. Still set when the settings are
        /// loaded means ReflowLink closed (or crashed, or lost power) during the write, so the pattern gets blocked.
        /// </summary>
        public int WritingPattern { get; set; } = -1;
        /// <summary>Set when loading found an interrupted write: the pattern that was blocked for it. Not saved.</summary>
        [XmlIgnore]
        public int? InterruptedWrite { get; private set; }
        public int LastSlot { get; set; } = 0;

        // Logging
        public bool AutoRecordRuns { get; set; } = true;
        public int RecordAfterRunSeconds { get; set; } = 60;
        public string LogFolder { get; set; } = "";
        /// <summary>
        /// Write CSV recordings with this PC's list separator and decimal mark (so Excel opens them in columns here) instead
        /// of commas and points.
        /// </summary>
        public bool CsvLocalFormat { get; set; }

        // Window
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 1360;
        public int WindowHeight { get; set; } = 860;
        public bool WindowMaximized { get; set; }

        [XmlIgnore]
        public StationModel Station => StationCatalog.Find(StationId);

        /// <summary>Set when the settings file couldn't be read: what happened, for the user. Not saved.</summary>
        [XmlIgnore]
        public string LoadWarning { get; private set; }

        /// <summary>Units the controller of the selected station is configured for.</summary>
        [XmlIgnore]
        public ControllerUnits Units => Station.CustomUnits ? new(GenericRampUnit, GenericDwellUnit) : Station.Units;

        [XmlIgnore]
        public bool StationChosen => !string.IsNullOrEmpty(StationId);

        /// <summary>Connection options for the top IR controller.</summary>
        public ConnectionOptions IrConnection() => new() {
            PortName = PortName,
            BaudRate = BaudRate,
            Address = Address,
            ResponseTimeoutMs = ResponseTimeoutMs,
            Retries = Retries,
            PollIntervalMs = PollIntervalMs,
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable,
            UseSimulator = UseSimulator,
            SimulatorSpeed = SimulatorSpeed,
            Units = Units,
            SimulatorLimit = Station.MaxTemperature,
        };

        public PatternSync SyncFor(int pattern) => PatternSyncs?.FirstOrDefault(p => p.Pattern == pattern);

        /// <summary>Remembers the Wait for Bed / Wait for IR choices (and targets) written to a pattern.</summary>
        /// <param name="withBedTargets">Save the bed targets too; false when no bed heater is used (they'd just be the IR targets).</param>
        public void SetSync(int pattern, ReflowProfile profile, bool withBedTargets = true) {
            PatternSyncs.RemoveAll(p => p.Pattern == pattern);
            if (profile != null) PatternSyncs.Add(PatternSync.From(pattern, profile, withBedTargets));
        }

        /// <summary>Selects a station and applies its temperature limit (unless <paramref name="keepLimit"/>).</summary>
        public void SelectStation(string id, bool keepLimit = false) {
            var station = StationCatalog.Find(id);
            if (station.Id != StationId) SegmentsPaired = false; // learned for the old station's controller
            StationId = station.Id;
            if (!keepLimit) MaxTemperature = station.MaxTemperature;
        }

        /// <summary>True when the temperature limit was changed from the selected station's default.</summary>
        [XmlIgnore]
        public bool CustomLimit => StationChosen && Math.Abs(MaxTemperature - Station.MaxTemperature) > 0.01;

        [XmlIgnore]
        public string EffectiveLogFolder =>
            string.IsNullOrWhiteSpace(LogFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReflowLink", "Logs")
                : LogFolder;

        public static string SettingsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReflowLink");

        public static string ProfilesFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReflowLink", "Profiles");

        private static string SettingsPath => Path.Combine(SettingsFolder, "settings.xml");

        // Folders used before the rename to ReflowLink.
        private static string LegacySettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IR6500 Studio", "settings.xml");

        private static string LegacyDocumentsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IR6500 Studio");

        private static string DocumentsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReflowLink");

        private static readonly XmlSerializer Serializer = new(typeof(AppSettings));


        /// <summary>
        /// Loads the settings. If the file can't be read, the copy kept from the previous save is used; if that fails too,
        /// the defaults are used. Either way <see cref="LoadWarning"/> says so, since the settings hold safety state (the
        /// blocked patterns, the bed targets and the temperature limits).
        /// </summary>
        public static AppSettings Load() => LoadFrom(SettingsPath, legacy: true);

        /// <summary>Loads from <paramref name="path"/>, falling back to its backup (path + ".bak").</summary>
        internal static AppSettings LoadFrom(string path, bool legacy = false) {
            string backupPath = path + ".bak";
            string error = null;
            if (File.Exists(path)) {
                try {
                    return Read(path).Sanitize();
                } catch (Exception ex) {
                    error = ex.GetBaseException().Message;
                    SetAside(path);
                }
            }
            if (File.Exists(backupPath)) {
                try {
                    var s = Read(backupPath).Sanitize();
                    // Also when the main file is missing: a save was interrupted between removing it and putting the new one in place.
                    s.LoadWarning = (error != null ? $"ReflowLink's settings file couldn't be read ({error})" : "ReflowLink's settings file was missing")
                        + ", so the copy kept from the save before it was used. The most recent changes may be missing: check Settings, "
                        + "and write your profiles to the controller again if a pattern was being written.";
                    return s;
                } catch (Exception ex) {
                    error ??= ex.GetBaseException().Message;
                }
            }
            if (error == null && legacy) {
                try {
                    if (File.Exists(LegacySettingsPath)) return MigrateFromIr6500Studio();
                } catch { /* old settings unreadable: start fresh */ }
            }
            var d = new AppSettings { SettingsVersion = CurrentVersion };
            if (error != null) {
                d.LoadWarning = $"ReflowLink's settings couldn't be read ({error}), so it started with the default settings. "
                    + "Check the station, connections and temperature limits in Settings. Patterns that were blocked after a rejected write "
                    + "and the bed targets saved per pattern are lost: write your profiles to the controller again before running them.";
            }
            return d;
        }

        /// <summary>Keeps an unreadable settings file for inspection, out of the way of the next save.</summary>
        private static void SetAside(string path) {
            try {
                string target = Path.Combine(Path.GetDirectoryName(path), $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.xml");
                File.Move(path, target);
            } catch {
                // It must not stay: the next save would turn it into the backup, over the good copy.
                try { File.Delete(path); } catch { }
            }
        }

        private static AppSettings Read(string path) {
            using var r = XmlReader.Create(path);
            return (AppSettings)Serializer.Deserialize(r);
        }

        /// <summary>
        /// One-time import from "IR6500 Studio": keeps all settings, selects the ACHI IR6500 and
        /// moves Documents\IR6500 Studio (profiles and logs) to Documents\ReflowLink.
        /// </summary>
        private static AppSettings MigrateFromIr6500Studio() {
            var s = Read(LegacySettingsPath);
            if (string.IsNullOrEmpty(s.StationId)) s.StationId = StationCatalog.DefaultId;
            try {
                if (Directory.Exists(LegacyDocumentsFolder) && !Directory.Exists(DocumentsFolder)) {
                    Directory.Move(LegacyDocumentsFolder, DocumentsFolder);
                    s.LastProfilePath = Relocate(s.LastProfilePath);
                    s.LogFolder = Relocate(s.LogFolder);
                }
            } catch { /* keep using the old folder if it can't be moved */ }
            s.Sanitize();
            s.Save();
            return s;
        }

        private static string Relocate(string path) =>
            !string.IsNullOrEmpty(path) && path.StartsWith(LegacyDocumentsFolder, StringComparison.OrdinalIgnoreCase)
                ? DocumentsFolder + path.Substring(LegacyDocumentsFolder.Length)
                : path;

        /// <summary>Saves the settings without ever leaving a half-written file; the previous file is kept as a backup.</summary>
        public void Save() {
            try { SaveTo(SettingsPath); } catch { /* settings are best-effort */ }
        }

        internal void SaveTo(string path) {
            SafeFile.Write(path, stream => {
                using var w = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true });
                Serializer.Serialize(w, this);
            }, path + ".bak");
        }

        internal AppSettings Sanitize() {
            // Before version 2 the stations defaulted to 230 °C (250 °C for the generic one). The controller's factory
            // setpoint limit is 260 °C, so a limit still at the old default moves to the new one. Other values are kept.
            if (SettingsVersion < 2 && StationChosen && MaxTemperature is 230 or 250 && Station.MaxTemperature > MaxTemperature)
                MaxTemperature = Station.MaxTemperature;
            SettingsVersion = CurrentVersion;

            if (Address is < 0 or > 99) Address = 1;
            if (BaudRate <= 0) BaudRate = 9600;
            ResponseTimeoutMs = Math.Max(100, Math.Min(5000, ResponseTimeoutMs));
            Retries = Math.Max(0, Math.Min(5, Retries));
            PollIntervalMs = Math.Max(200, Math.Min(10000, PollIntervalMs));
            if (MaxTemperature is <= 0 or > 500) MaxTemperature = Station.MaxTemperature;
            if (SimulatorSpeed is < 1 or > 50) SimulatorSpeed = 1;
            if (LastSlot is < 0 or > 9) LastSlot = 0;
            BlockedPatterns = [.. (BlockedPatterns ?? []).Where(p => p is >= 0 and <= 9).Distinct()];
            if (WritingPattern is >= 0 and <= 9) {
                InterruptedWrite = WritingPattern;
                if (!BlockedPatterns.Contains(WritingPattern)) BlockedPatterns.Add(WritingPattern);
            }
            WritingPattern = -1;
            Bed = (Bed ?? new BedSettings()).Sanitize();
            PatternSyncs = [.. (PatternSyncs ?? []).Where(p => p != null && p.Pattern is >= 0 and <= 9).GroupBy(p => p.Pattern).Select(g => g.Last())];
            RecordAfterRunSeconds = Math.Max(0, Math.Min(3600, RecordAfterRunSeconds));
            MaxWaitMinutes = double.IsNaN(MaxWaitMinutes) ? 10 : Math.Max(0, Math.Min(240, MaxWaitMinutes));
            return this;
        }
    }

    /// <summary>P, I and D saved before an auto-tune replaced them.</summary>
    public sealed class TuningBackup {
        [XmlAttribute("p")] public double P { get; set; }
        [XmlAttribute("i")] public double I { get; set; }
        [XmlAttribute("d")] public double D { get; set; }
        [XmlAttribute("saved")] public DateTime Saved { get; set; }
    }

    /// <summary>How to reach one PC410 (built from the settings; not saved).</summary>
    public sealed class ConnectionOptions {
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; } = 9600;
        public int Address { get; set; } = 1;
        public int ResponseTimeoutMs { get; set; } = 500;
        public int Retries { get; set; } = 2;
        public int PollIntervalMs { get; set; } = 500;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public bool UseSimulator { get; set; }
        public double SimulatorSpeed { get; set; } = 1;
        public ControllerUnits Units { get; set; } = ControllerUnits.SecondsBased;
        /// <summary>Setpoint high limit (HS) the simulator reports.</summary>
        public double SimulatorLimit { get; set; } = StationModel.DefaultMaxTemperature;
    }

    /// <summary>How the bottom (bed) heater is wired.</summary>
    public enum BedMode {
        /// <summary>Top IR only.</summary>
        None,
        /// <summary>
        /// The top PC410 reads the bed thermocouple on IN2 (P2) and gates the bed controller's output through its OUT2
        /// relay, switched by rewriting alarm 2 (A2). ReflowLink regulates the bed.
        /// </summary>
        SinglePc410,
        /// <summary>A second PC410 with its own serial port controls the bed.</summary>
        SecondPc410,
    }

    /// <summary>Regulation used in <see cref="BedMode.SinglePc410"/>: ReflowLink switches OUT2 on and off.</summary>
    public sealed class BedSwitchSettings {
        /// <summary>Full power when the bed is this far below its setpoint (°C).</summary>
        public double ProportionalBand { get; set; } = 15;
        /// <summary>Integral time (s); 0 switches the integral off.</summary>
        public double IntegralSeconds { get; set; } = 240;
        /// <summary>Length of one on/off cycle (s).</summary>
        public double CycleSeconds { get; set; } = 10;
        /// <summary>A2 writes made over the lifetime of these settings (each one switches OUT2).</summary>
        public long TotalWrites { get; set; }

        internal BedSwitchSettings Sanitize() {
            ProportionalBand = Math.Max(1, Math.Min(200, ProportionalBand));
            IntegralSeconds = Math.Max(0, Math.Min(3600, IntegralSeconds));
            CycleSeconds = Math.Max(4, Math.Min(120, CycleSeconds));
            TotalWrites = Math.Max(0, TotalWrites);
            return this;
        }
    }

    /// <summary>Settings of the bottom (bed) heater: a second PC410, or the top PC410's IN2 and OUT2.</summary>
    public sealed class BedSettings : IConnectionSettings {
        /// <summary>Bed heat hardware chosen in Settings.</summary>
        public BedMode Mode { get; set; }

        /// <summary>
        /// "Use Bed Heat Control" of version 2.0.0, which only knew the second PC410. Kept in step with <see cref="Mode"/>
        /// so the file still reads correctly in that version.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Regulation for <see cref="BedMode.SinglePc410"/>.</summary>
        public BedSwitchSettings Switching { get; set; } = new();
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; } = 9600;
        public int Address { get; set; } = 1;
        public int ResponseTimeoutMs { get; set; } = 500;
        public int Retries { get; set; } = 2;
        public int PollIntervalMs { get; set; } = 500;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public bool UseSimulator { get; set; }
        public double SimulatorSpeed { get; set; } = 1;
        /// <summary>A separately bought PC410 is treated as generic; ACHI-style seconds are the default.</summary>
        public RampUnit RampUnit { get; set; } = RampUnit.PerSecond;
        public DwellUnit DwellUnit { get; set; } = DwellUnit.Seconds;
        public double MaxTemperature { get; set; } = StationModel.DefaultMaxTemperature;
        /// <summary>How segments after the last step are cleared on the bed controller.</summary>
        public UnusedStepMode UnusedSteps { get; set; } = UnusedStepMode.Zeros;
        /// <summary>Read the bed pattern back after writing and compare it.</summary>
        public bool VerifyAfterWrite { get; set; } = true;
        /// <summary>Show the second PC410's second input (IN2, P2) as "IN2 (Bed Heat)": information only.</summary>
        public bool ReadIn2 { get; set; }

        [XmlIgnore]
        public ControllerUnits Units => new(RampUnit, DwellUnit);

        public ConnectionOptions Connection() => new() {
            PortName = PortName,
            BaudRate = BaudRate,
            Address = Address,
            ResponseTimeoutMs = ResponseTimeoutMs,
            Retries = Retries,
            PollIntervalMs = PollIntervalMs,
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable,
            UseSimulator = UseSimulator,
            SimulatorSpeed = SimulatorSpeed,
            Units = Units,
            SimulatorLimit = MaxTemperature,
        };

        internal BedSettings Sanitize() {
            // Files from 2.0.0 have no Mode: "Use Bed Heat Control" meant the second PC410.
            if (Mode == BedMode.None && Enabled) Mode = BedMode.SecondPc410;
            if (!Enum.IsDefined(typeof(BedMode), Mode)) Mode = BedMode.None;
            Enabled = Mode == BedMode.SecondPc410;
            Switching = (Switching ?? new BedSwitchSettings()).Sanitize();
            if (Address is < 0 or > 99) Address = 1;
            if (BaudRate <= 0) BaudRate = 9600;
            ResponseTimeoutMs = Math.Max(100, Math.Min(5000, ResponseTimeoutMs));
            Retries = Math.Max(0, Math.Min(5, Retries));
            PollIntervalMs = Math.Max(200, Math.Min(10000, PollIntervalMs));
            if (MaxTemperature is <= 0 or > 500) MaxTemperature = StationModel.DefaultMaxTemperature;
            if (SimulatorSpeed is < 1 or > 50) SimulatorSpeed = 1;
            PortName ??= "";
            return this;
        }
    }

    /// <summary>Wait for Bed / Wait for IR choices, holdbacks and targets last written to a pattern.</summary>
    public sealed class PatternSync {
        [XmlAttribute("pattern")] public int Pattern { get; set; }
        /// <summary>Bit n set: step n + 1 has Wait for Bed.</summary>
        [XmlAttribute("waitForBed")] public int WaitForBedMask { get; set; }
        /// <summary>Bit n set: step n + 1 has Wait for IR.</summary>
        [XmlAttribute("waitForIr")] public int WaitForIrMask { get; set; }
        [XmlAttribute("steps")] public int Steps { get; set; }
        [XmlAttribute("irHoldback")] public double IrHoldback { get; set; } = ReflowProfile.HoldbackDefault;
        [XmlAttribute("bedHoldback")] public double BedHoldback { get; set; } = ReflowProfile.HoldbackDefault;
        /// <summary>IR targets of the steps ("120;160;205"), to tell whether the pattern still holds the profile.</summary>
        [XmlAttribute("irTargets")] public string IrTargetList { get; set; } = "";
        /// <summary>Bed targets of the steps. With a single PC410 no controller stores them, so ReflowLink does.</summary>
        [XmlAttribute("bedTargets")] public string BedTargetList { get; set; } = "";

        /// <summary>True when any step waits for the other heater.</summary>
        public bool Any => (WaitForBedMask | WaitForIrMask) != 0;

        [XmlIgnore]
        public IReadOnlyList<double> IrTargets => ParseList(IrTargetList);

        [XmlIgnore]
        public IReadOnlyList<double> BedTargets => ParseList(BedTargetList);

        /// <summary>A bed target and an IR target were saved for every step (written by version 2.1 or later).</summary>
        public bool HasTargets => Steps > 0 && IrTargets.Count == Steps && BedTargets.Count == Steps;

        /// <summary>True when <paramref name="p"/> (read from the controller) has the steps and IR targets saved here.</summary>
        public bool Matches(ReflowProfile p) {
            if (p == null || !HasTargets || p.Steps.Count != Steps) return false;
            var ir = IrTargets;
            for (int i = 0; i < Steps; i++)
                if (Math.Abs(p.Steps[i].Target - ir[i]) > 0.15) return false;
            return true;
        }

        private static List<double> ParseList(string s) {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            foreach (var part in s.Split(';')) {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsNaN(v) || double.IsInfinity(v)) return [];
                list.Add(v);
            }
            return list;
        }

        private static string JoinList(IEnumerable<double> values) =>
            string.Join(";", values.Select(v => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture)));

        public bool WaitForBed(int step) => step is >= 1 and <= 30 && (WaitForBedMask & (1 << (step - 1))) != 0;
        public bool WaitForIr(int step) => step is >= 1 and <= 30 && (WaitForIrMask & (1 << (step - 1))) != 0;

        public static PatternSync From(int pattern, ReflowProfile p, bool withBedTargets = true) {
            var s = new PatternSync {
                Pattern = pattern,
                Steps = p.Steps.Count,
                IrHoldback = p.Holdback ?? ReflowProfile.HoldbackDefault,
                BedHoldback = p.BedHoldback ?? ReflowProfile.HoldbackDefault,
                IrTargetList = JoinList(p.Steps.Select(st => st.Target)),
                BedTargetList = withBedTargets ? JoinList(p.Steps.Select(st => st.BedTarget)) : "",
            };
            for (int i = 0; i < p.Steps.Count; i++) {
                if (p.Steps[i].WaitForBed) s.WaitForBedMask |= 1 << i;
                if (p.Steps[i].WaitForIr) s.WaitForIrMask |= 1 << i;
            }
            return s;
        }

        /// <summary>
        /// Copies the saved choices onto a profile read back from the controllers. Nothing is copied when the pattern no
        /// longer has the same number of steps (it was changed elsewhere). Returns whether the choices were applied.
        /// </summary>
        /// <param name="includeBed">
        /// Also copy the bed targets and bed holdback (single PC410, where no controller stores them). They are only copied
        /// when the IR targets still match, so they can't be paired with a pattern changed elsewhere.
        /// </param>
        public bool ApplyTo(ReflowProfile p, bool includeBed = false) {
            if (p.Steps.Count != Steps) return false;
            if (includeBed && !Matches(p)) return false;
            var bed = BedTargets;
            for (int i = 0; i < p.Steps.Count; i++) {
                p.Steps[i].WaitForBed = WaitForBed(i + 1);
                p.Steps[i].WaitForIr = WaitForIr(i + 1);
                if (includeBed) p.Steps[i].BedTarget = bed[i];
            }
            if (includeBed) p.BedHoldback = BedHoldback;
            return true;
        }
    }

    /// <summary>
    /// Writes telemetry to a CSV file: commas and decimal points by default, or this PC's list separator and decimal mark
    /// so Excel opens it in columns. Optional "# " lines before the column names describe the run.
    /// </summary>
    public sealed class CsvRecorder : IDisposable {
        /// <summary>Lines before the column names start with this.</summary>
        public const string MetadataPrefix = "# ";

        private readonly StreamWriter _w;
        private readonly DateTime _start;
        private readonly bool _withBed, _withOut2, _withIrIn2, _withBedIn2;
        private readonly CultureInfo _culture;
        private readonly string _sep;

        /// <param name="withBed">Adds the bed heat controller's columns.</param>
        /// <param name="withOut2">Adds whether OUT2 was switched on (single PC410 bed control).</param>
        /// <param name="withIrIn2">Adds the Top IR controller's IN2 reading (information only).</param>
        /// <param name="withBedIn2">Adds the second PC410's IN2 reading (information only).</param>
        /// <param name="localFormat">Use this PC's list separator and decimal mark instead of commas and points.</param>
        /// <param name="metadata">Lines describing the run (station, pattern, profile…), written before the column names.</param>
        public CsvRecorder(string folder, string label, bool withBed = false, bool withOut2 = false, bool withIrIn2 = false, bool withBedIn2 = false,
            bool localFormat = false, IEnumerable<string> metadata = null) {
            Directory.CreateDirectory(folder);
            _start = DateTime.Now;
            _withBed = withBed;
            _withOut2 = withBed && withOut2;
            _withIrIn2 = withIrIn2;
            _withBedIn2 = withBedIn2;
            (_culture, _sep) = Format(localFormat);
            string safe = string.IsNullOrWhiteSpace(label) ? "session" : MakeSafe(label);
            // Never overwrite an earlier recording (two runs started within the same second get a suffix).
            FileStream fs = null;
            for (int n = 0; fs == null; n++) {
                FilePath = Path.Combine(folder, $"{_start:yyyy-MM-dd_HHmmss}_{safe}{(n == 0 ? "" : "_" + n)}.csv");
                try {
                    fs = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                } catch (IOException) when (n < 100 && File.Exists(FilePath)) { }
            }
            _w = new StreamWriter(fs, new UTF8Encoding(true));
            foreach (var line in metadata ?? []) _w.WriteLine(MetadataPrefix + line.Replace("\r", " ").Replace("\n", " "));
            var columns = new List<string> { "timestamp", "elapsed_s", "pv_c", "sp_c", "output_pct", "state", "segment" };
            if (withBed) columns.AddRange(["bed_pv_c", "bed_sp_c", "bed_output_pct", "bed_state", "bed_segment", "waiting"]);
            if (_withOut2) columns.Add("bed_out2");
            if (_withIrIn2) columns.Add("in2_top_ir_c");
            if (_withBedIn2) columns.Add("in2_bed_heat_c");
            _w.WriteLine(string.Join(_sep, columns));
            _w.Flush();
        }

        /// <summary>Culture and separator for the numbers; a separator equal to the decimal mark falls back to ';'.</summary>
        public static (CultureInfo Culture, string Separator) Format(bool local) {
            if (!local) return (CultureInfo.InvariantCulture, ",");
            var c = CultureInfo.CurrentCulture;
            string sep = c.TextInfo.ListSeparator;
            if (string.IsNullOrEmpty(sep) || sep == c.NumberFormat.NumberDecimalSeparator) sep = ";";
            return (c, sep);
        }

        public string FilePath { get; private set; }
        public int Rows { get; private set; }

        /// <summary>Writes one row: the IR reading, and the latest bed reading when bed columns are recorded.</summary>
        /// <param name="out2">OUT2 state for the bed_out2 column: true = on, false = off, null = unknown.</param>
        public void Write(Telemetry t, Telemetry bed = null, string waiting = "", bool? out2 = null) {
            var c = _culture;
            string sep = _sep;
            var line = new StringBuilder(string.Join(sep,
                t.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                (t.Time - _start).TotalSeconds.ToString("0.0", c),
                t.Pv?.ToString("0.0", c) ?? "",
                t.Sp?.ToString("0.0", c) ?? "",
                t.Op?.ToString("0", c) ?? "",
                t.State.ToString(),
                t.Segment?.ToString(c) ?? ""));
            if (_withBed) {
                line.Append(sep).Append(string.Join(sep,
                    bed?.Pv?.ToString("0.0", c) ?? "",
                    bed?.Sp?.ToString("0.0", c) ?? "",
                    bed?.Op?.ToString("0", c) ?? "",
                    bed?.State.ToString() ?? "",
                    bed?.Segment?.ToString(c) ?? "",
                    waiting ?? ""));
            }
            if (_withOut2) line.Append(sep).Append(out2 is bool on ? (on ? "1" : "0") : "");
            if (_withIrIn2) line.Append(sep).Append(t.Pv2?.ToString("0.0", c) ?? "");
            if (_withBedIn2) line.Append(sep).Append(bed?.Pv2?.ToString("0.0", c) ?? "");
            _w.WriteLine(line.ToString());
            Rows++;
            // Every row reaches the disk: a crash or power cut loses at most the last reading.
            _w.Flush();
        }

        private static string MakeSafe(string s) {
            var sb = new StringBuilder();
            foreach (char ch in s)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 || ch == ' ' ? '_' : ch);
            string r = sb.ToString();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }

        public void Dispose() {
            try { _w.Flush(); _w.Dispose(); } catch { }
        }
    }
}
