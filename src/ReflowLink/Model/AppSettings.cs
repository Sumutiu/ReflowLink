// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ReflowLink.Protocol;

namespace ReflowLink.Model {
    public sealed class AppSettings {
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

        // Profiles
        public double MaxTemperature { get; set; } = 230;
        public UnusedStepMode UnusedSteps { get; set; } = UnusedStepMode.Zeros;
        public bool VerifyAfterWrite { get; set; } = true;
        public double PreviewStartTemperature { get; set; } = 25;
        public string LastProfilePath { get; set; } = "";
        public int LastSlot { get; set; } = 0;

        // Logging
        public bool AutoRecordRuns { get; set; } = true;
        public int RecordAfterRunSeconds { get; set; } = 60;
        public string LogFolder { get; set; } = "";

        // Window
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; } = 1360;
        public int WindowHeight { get; set; } = 860;
        public bool WindowMaximized { get; set; }

        [XmlIgnore]
        public StationModel Station => StationCatalog.Find(StationId);

        /// <summary>Units the controller of the selected station is configured for.</summary>
        [XmlIgnore]
        public ControllerUnits Units => Station.CustomUnits ? new(GenericRampUnit, GenericDwellUnit) : Station.Units;

        [XmlIgnore]
        public bool StationChosen => !string.IsNullOrEmpty(StationId);

        /// <summary>Selects a station and applies its temperature limit.</summary>
        public void SelectStation(string id) {
            var station = StationCatalog.Find(id);
            StationId = station.Id;
            MaxTemperature = station.MaxTemperature;
        }

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

        public static AppSettings Load() {
            try {
                if (File.Exists(SettingsPath)) return Read(SettingsPath).Sanitize();
                if (File.Exists(LegacySettingsPath)) return MigrateFromIr6500Studio();
            } catch { /* corrupt settings: fall back to defaults */ }
            return new AppSettings();
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

        public void Save() {
            try {
                Directory.CreateDirectory(SettingsFolder);
                using var w = XmlWriter.Create(SettingsPath, new XmlWriterSettings { Indent = true });
                Serializer.Serialize(w, this);
            } catch { /* settings are best-effort */ }
        }

        private AppSettings Sanitize() {
            if (Address is < 0 or > 99) Address = 1;
            if (BaudRate <= 0) BaudRate = 9600;
            ResponseTimeoutMs = Math.Max(100, Math.Min(5000, ResponseTimeoutMs));
            Retries = Math.Max(0, Math.Min(5, Retries));
            PollIntervalMs = Math.Max(200, Math.Min(10000, PollIntervalMs));
            if (MaxTemperature is <= 0 or > 500) MaxTemperature = Station.MaxTemperature;
            if (SimulatorSpeed is < 1 or > 50) SimulatorSpeed = 1;
            if (LastSlot is < 0 or > 9) LastSlot = 0;
            RecordAfterRunSeconds = Math.Max(0, Math.Min(3600, RecordAfterRunSeconds));
            return this;
        }
    }

    /// <summary>Writes telemetry to a CSV file (invariant culture, Excel-friendly).</summary>
    public sealed class CsvRecorder : IDisposable {
        private readonly StreamWriter _w;
        private readonly DateTime _start;

        public CsvRecorder(string folder, string label) {
            Directory.CreateDirectory(folder);
            _start = DateTime.Now;
            string safe = string.IsNullOrWhiteSpace(label) ? "session" : MakeSafe(label);
            FilePath = Path.Combine(folder, $"{_start:yyyy-MM-dd_HHmmss}_{safe}.csv");
            _w = new StreamWriter(FilePath, false, new UTF8Encoding(true));
            _w.WriteLine("timestamp,elapsed_s,pv_c,sp_c,output_pct,state,segment");
            _w.Flush();
        }

        public string FilePath { get; }
        public int Rows { get; private set; }

        public void Write(Telemetry t) {
            var c = CultureInfo.InvariantCulture;
            _w.WriteLine(string.Join(",",
                t.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", c),
                (t.Time - _start).TotalSeconds.ToString("0.0", c),
                t.Pv?.ToString("0.0", c) ?? "",
                t.Sp?.ToString("0.0", c) ?? "",
                t.Op?.ToString("0", c) ?? "",
                t.State.ToString(),
                t.Segment?.ToString(c) ?? ""));
            Rows++;
            if (Rows % 10 == 0) _w.Flush();
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
