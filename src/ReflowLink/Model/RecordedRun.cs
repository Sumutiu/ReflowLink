// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReflowLink.Model {
    /// <summary>
    /// Temperatures read back from a CSV recording, to lay a past run over a profile's preview. Time 0 is the first row
    /// where the program was running (or the first row, for a recording made without a run).
    /// </summary>
    public sealed class RecordedRun {
        public string Name { get; private set; } = "";
        public List<CurvePoint> Ir { get; } = [];
        public List<CurvePoint> Bed { get; } = [];

        /// <summary>Reads a ReflowLink CSV recording (commas or this PC's list separator, with or without "# " lines).</summary>
        public static RecordedRun Load(string path) {
            var lines = File.ReadAllLines(path);
            int header = Array.FindIndex(lines, l => !l.StartsWith(CsvRecorder.MetadataPrefix, StringComparison.Ordinal) && l.Trim().Length > 0);
            if (header < 0) throw new InvalidDataException("The file has no column names.");
            string head = lines[header];
            char sep = head.Contains(';') ? ';' : head.Contains('\t') ? '\t' : ',';
            var cols = head.Split(sep).Select(c => c.Trim().ToLowerInvariant()).ToList();
            int iTime = cols.IndexOf("elapsed_s"), iPv = cols.IndexOf("pv_c"), iBed = cols.IndexOf("bed_pv_c"), iState = cols.IndexOf("state");
            if (iTime < 0 || iPv < 0) throw new InvalidDataException("This isn't a ReflowLink recording (no elapsed_s / pv_c columns).");

            var run = new RecordedRun { Name = Path.GetFileNameWithoutExtension(path) };
            double? t0 = null, first = null;
            var rows = new List<(double T, double? Pv, double? Bed, bool Running)>();
            for (int i = header + 1; i < lines.Length; i++) {
                var f = lines[i].Split(sep);
                if (f.Length <= Math.Max(iTime, iPv) || !TryNumber(f[iTime], sep, out double t)) continue;
                double? pv = TryNumber(f[iPv], sep, out double v) ? v : null;
                double? bed = iBed >= 0 && iBed < f.Length && TryNumber(f[iBed], sep, out double b) ? b : null;
                bool running = iState >= 0 && iState < f.Length && f[iState].Trim() is "Running" or "Hold";
                first ??= t;
                if (running) t0 ??= t;
                rows.Add((t, pv, bed, running));
            }
            if (rows.Count == 0) throw new InvalidDataException("The recording has no readings.");
            double zero = t0 ?? first ?? 0;
            foreach (var (t, pv, bed, _) in rows) {
                if (pv.HasValue) run.Ir.Add(new CurvePoint(t - zero, pv.Value));
                if (bed.HasValue) run.Bed.Add(new CurvePoint(t - zero, bed.Value));
            }
            return run;
        }

        private static bool TryNumber(string s, char sep, out double v) {
            s = s.Trim();
            // With ';' or tab as separator the decimal mark may be a comma.
            if (sep != ',') s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
