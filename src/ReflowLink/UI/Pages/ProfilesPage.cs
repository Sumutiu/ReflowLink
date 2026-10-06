// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>Small caption/value tile used under the preview chart.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class MiniStat : ThemedControl {
        private string _value = "—";

        public MiniStat(string caption) {
            Text = caption;
            Height = Theme.S(54);
        }

        public string Value { get => _value; set { _value = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Sf(8)))
            using (var b = new SolidBrush(Theme.SurfaceRaised))
                g.FillPath(b, p);
            int pad = Theme.S(12);
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.SmallBold, new Rectangle(pad, Theme.S(7), Width - pad * 2, Theme.S(18)), Theme.TextFaint, Theme.LeftMiddle);
            TextRenderer.DrawText(g, _value, Theme.Heading, new Rectangle(pad, Theme.S(24), Width - pad * 2, Theme.S(24)), Theme.Text, Theme.LeftMiddle);
        }
    }

    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class ProfilesPage : PageBase {
        private sealed class StepRow {
            public ThemedLabel Number;
            public ToggleSwitch Use;
            public NumberBox Ramp, Target, Hold;
            public ThemedLabel Time;
        }

        private readonly List<StepRow> _rows = [];
        private readonly ModernTextBox _name;
        private readonly ModernDropDown _slot;
        private readonly ModernButton _read, _write, _new, _open, _save, _saveAs;
        private readonly ToggleSwitch _useHoldback;
        private readonly NumberBox _holdback, _startTemp;
        private readonly ThemedLabel _validation;
        private readonly CardHeader _editorHeader;
        private readonly ChartView _chart;
        private readonly ChartSeries _curve;
        private readonly MiniStat _statTotal, _statPeak, _statSteps, _statRamp;
        private readonly Timer _previewTimer = new() { Interval = 120 };

        private string _filePath = "";
        private string _notes = "";
        private bool _dirty, _loading;

        public ProfilesPage(AppState state) : base(state, "Profiles", "Create, edit and transfer reflow profiles") {
            // ---------------------------------------------------------------- toolbar
            _name = new ModernTextBox {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = Padding.Empty,
                Placeholder = "Profile name"
            };
            _name.TextChanged += (s, e) => MarkDirty();

            _new = new ModernButton("New", Glyph.Plus) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _new.Click += (s, e) => ShowNewMenu();
            _open = new ModernButton("Open", Glyph.Folder) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _open.Click += (s, e) => OpenProfile();
            _save = new ModernButton("Save", Glyph.Save) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _save.Click += (s, e) => SaveProfile(false);
            _saveAs = new ModernButton("Save as…", Glyph.None) { Margin = Padding.Empty };
            _saveAs.Click += (s, e) => SaveProfile(true);

            var left = Flow();
            left.Controls.AddRange([_new, _open, _save, _saveAs]);

            _slot = new ModernDropDown { Width = Theme.S(230), Margin = new Padding(0, 0, Theme.S(8), 0) };
            _slot.SetItems(State.SlotLabels(), State.Settings.LastSlot);
            _slot.SelectedIndexChanged += (s, e) => State.Settings.LastSlot = _slot.SelectedIndex;
            _read = new ModernButton("Read", Glyph.Download) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _read.Click += async (s, e) => await ReadFromController();
            _write = new ModernButton("Write to controller", Glyph.Upload, ButtonKind.Primary) { Margin = Padding.Empty };
            _write.Click += async (s, e) => await WriteToController();
            var tip = Theme.CreateToolTip();
            tip.SetToolTip(_read, "Read the selected pattern from the controller into the editor");
            tip.SetToolTip(_write, "Store the edited profile in the selected controller pattern");

            var right = Flow();
            right.Controls.AddRange([_slot, _read, _write]);

            var bar = Table(3, 1);
            bar.Dock = DockStyle.Fill;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Anchor = AnchorStyles.Left;
            right.Anchor = AnchorStyles.Right;
            bar.Controls.Add(left, 0, 0);
            bar.Controls.Add(right, 2, 0);
            var toolbar = new CardPanel { Dock = DockStyle.Fill, Padding = Theme.Pad(14, 12, 14, 12), Margin = new Padding(0, 0, 0, Theme.S(16)) };
            toolbar.Controls.Add(bar);

            // ---------------------------------------------------------------- editor card
            var editor = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(8), 0), AutoScroll = true };
            _editorHeader = new CardHeader("Profile", "Not saved yet");
            var grid = Table(6, Pc410Protocol.SegmentCount + 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            int[] widths = [30, 52, 114, 114, 98, 58];
            foreach (var w in widths) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(w)));
            string[] heads = ["#", "Use", "Ramp", "Target", "Hold", "Time"];
            for (int c = 0; c < heads.Length; c++) {
                var h = new ThemedLabel(heads[c].ToUpperInvariant(), Theme.SmallBold, Theme.TextFaint) { Margin = new Padding(Theme.S(2), 0, 0, Theme.S(6)) };
                if (c is >= 2 and <= 4) { h.AutoSize = false; h.Width = Theme.S(widths[c] - 12); h.TextAlign = ContentAlignment.MiddleRight; h.Height = Theme.S(18); }
                grid.Controls.Add(h, c, 0);
            }
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < Pc410Protocol.SegmentCount; i++) {
                int idx = i;
                var r = new StepRow {
                    Number = new ThemedLabel((i + 1).ToString(), Theme.BodyBold, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                    Use = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = Padding.Empty },
                    Ramp = new NumberBox(0.01, 10, 2, 0.1) { Suffix = "°C/s", Width = Theme.S(106), Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) },
                    Target = new NumberBox(0, 400, 1, 1) { Suffix = "°C", Width = Theme.S(106), Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) },
                    Hold = new NumberBox(0, 9999, 0, 5) { Suffix = "s", Width = Theme.S(90), Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) },
                    Time = new ThemedLabel("", Theme.Small, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                };
                r.Use.CheckedChanged += (s, e) => OnUseChanged(idx);
                r.Ramp.TextChanged += (s, e) => MarkDirty();
                r.Target.TextChanged += (s, e) => MarkDirty();
                r.Hold.TextChanged += (s, e) => MarkDirty();
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(r.Number, 0, i + 1);
                grid.Controls.Add(r.Use, 1, i + 1);
                grid.Controls.Add(r.Ramp, 2, i + 1);
                grid.Controls.Add(r.Target, 3, i + 1);
                grid.Controls.Add(r.Hold, 4, i + 1);
                grid.Controls.Add(r.Time, 5, i + 1);
                _rows.Add(r);
            }

            // Holdback + validation below the grid
            var extras = Table(1, 0);
            extras.Dock = DockStyle.Top;
            extras.AutoSize = true;
            extras.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            extras.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var hbRow = Flow();
            _useHoldback = new ToggleSwitch("Set holdback band") { Margin = new Padding(0, Theme.S(3), Theme.S(12), 0) };
            _useHoldback.CheckedChanged += (s, e) => { _holdback.Enabled = _useHoldback.Checked; MarkDirty(); };
            _holdback = new NumberBox(0, 100, 1, 0.5) { Suffix = "°C", Width = Theme.S(100), Enabled = false, Margin = Padding.Empty };
            _holdback.TextChanged += (s, e) => MarkDirty();
            hbRow.Controls.Add(_useHoldback);
            hbRow.Controls.Add(_holdback);
            var hbHint = new ThemedLabel("Holdback pauses the program while the temperature lags the setpoint by more than this band. Leave off to keep the controller's value.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(460), 0), Margin = new Padding(0, Theme.S(6), 0, 0) };
            _validation = new ThemedLabel("", Theme.Small, Theme.Red) { AutoSize = true, MaximumSize = new Size(Theme.S(460), 0), Margin = new Padding(0, Theme.S(10), 0, 0) };
            extras.Controls.Add(new Divider { Dock = DockStyle.None, Width = Theme.S(466), Margin = new Padding(0, Theme.S(6), 0, Theme.S(6)) }, 0, 0);
            extras.Controls.Add(hbRow, 0, 1);
            extras.Controls.Add(hbHint, 0, 2);
            extras.Controls.Add(_validation, 0, 3);

            var nameRow = Table(2, 1);
            nameRow.Dock = DockStyle.Top;
            nameRow.Height = Theme.S(34 + 18);
            nameRow.Padding = new Padding(0, 0, 0, Theme.S(18));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(82)));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(384)));
            nameRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new ThemedLabel("Name", Theme.Body, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) }, 0, 0);
            nameRow.Controls.Add(_name, 1, 0);

            editor.Controls.Add(extras);
            editor.Controls.Add(grid);
            editor.Controls.Add(nameRow);
            editor.Controls.Add(_editorHeader);
            Theme.ApplyDarkScrollbars(editor);

            // ---------------------------------------------------------------- preview card
            _curve = new ChartSeries { Name = "Planned setpoint", Color = Theme.Accent, Width = 2.2f, FillArea = true };
            _chart = new ChartView { Dock = DockStyle.Fill, ShowSecondaryAxis = false, EmptyText = "Add a step to see the curve", ShowLegend = false };
            _chart.Series.Add(_curve);

            _startTemp = new NumberBox(0, 200, 0, 1) {
                Suffix = "°C",
                Width = Theme.S(90),
                Margin = Padding.Empty,
                Value = State.Settings.PreviewStartTemperature
            };
            _startTemp.TextChanged += (s, e) => {
                if (_startTemp.IsValid) State.Settings.PreviewStartTemperature = _startTemp.Value.Value;
                SchedulePreview();
            };
            var previewHeader = new CardHeader("Preview", "Ideal setpoint path");
            previewHeader.Tools.Controls.Add(new ThemedLabel("Start at", Theme.Small, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(8), 0) });
            previewHeader.Tools.Controls.Add(_startTemp);

            _statTotal = new MiniStat("Total") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(6), 0) };
            _statPeak = new MiniStat("Peak") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(3), 0, Theme.S(3), 0) };
            _statRamp = new MiniStat("Ramping") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(3), 0, Theme.S(3), 0) };
            _statSteps = new MiniStat("Steps") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(6), 0, 0, 0) };
            var stats = Table(4, 1);
            stats.Dock = DockStyle.Bottom;
            stats.Height = Theme.S(54 + 12);
            stats.Padding = new Padding(0, Theme.S(12), 0, 0);
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Controls.Add(_statTotal, 0, 0);
            stats.Controls.Add(_statPeak, 1, 0);
            stats.Controls.Add(_statRamp, 2, 0);
            stats.Controls.Add(_statSteps, 3, 0);

            var preview = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0) };
            preview.Controls.Add(_chart);
            preview.Controls.Add(stats);
            preview.Controls.Add(previewHeader);
            _chart.BringToFront();

            // ---------------------------------------------------------------- root
            var main = Table(2, 1);
            main.Dock = DockStyle.Fill;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(524)));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(editor, 0, 0);
            main.Controls.Add(preview, 1, 0);

            var root = Table(1, 2);
            root.Dock = DockStyle.Fill;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(34 + 24 + 16)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(main, 0, 1);
            Controls.Add(root);

            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); UpdatePreview(); };
            State.SlotProfilesChanged += () => _slot.SetItems(State.SlotLabels(), _slot.SelectedIndex);
            State.BusyChanged += UpdateButtons;
            State.StationChanged += UpdatePreview;
            State.Device.StateChanged += (s, e) => UpdateButtons();

            // Initial profile: last opened file, else the leaded preset.
            ReflowProfile initial = null;
            if (!string.IsNullOrEmpty(State.Settings.LastProfilePath) && File.Exists(State.Settings.LastProfilePath)) {
                try { initial = ReflowProfile.Load(State.Settings.LastProfilePath); _filePath = State.Settings.LastProfilePath; } catch { initial = null; }
            }
            LoadProfile(initial ?? ProfilePresets.All[0].Create(), initial != null ? _filePath : "");
            UpdateButtons();
        }

        // ==================================================================== editor <-> model

        private void LoadProfile(ReflowProfile p, string path) {
            _loading = true;
            _name.Text = p.Name ?? "";
            _notes = p.Notes ?? "";
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                var s = i < p.Steps.Count ? p.Steps[i] : null;
                r.Use.Checked = s != null || i == 0;
                var src = s ?? DefaultStepAfter(p, i);
                r.Ramp.Value = src.RampRate;
                r.Target.Value = src.Target;
                r.Hold.Value = src.HoldSeconds;
            }
            _useHoldback.Checked = p.Holdback.HasValue;
            _holdback.Value = p.Holdback ?? 5;
            _holdback.Enabled = p.Holdback.HasValue;
            _filePath = path ?? "";
            _loading = false;
            ApplyRowStates();
            _dirty = false;
            UpdateHeader();
            UpdatePreview();
        }

        private static ProfileStep DefaultStepAfter(ReflowProfile p, int index) {
            var last = p.Steps.Count > 0 ? p.Steps[p.Steps.Count - 1] : null;
            double target = last != null ? Math.Min(last.Target + 10 * (index - p.Steps.Count + 1), 230) : 150;
            return new ProfileStep(1.0, target, 20);
        }

        private ReflowProfile BuildProfile(List<string> errors) {
            var p = new ReflowProfile { Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled profile" : _name.Text.Trim(), Notes = _notes };
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                if (!r.Use.Checked) break;
                if (!r.Ramp.IsValid) errors?.Add($"Step {i + 1}: ramp rate must be between 0.01 and 10 °C/s.");
                if (!r.Target.IsValid) errors?.Add($"Step {i + 1}: enter a valid target temperature.");
                if (!r.Hold.IsValid) errors?.Add($"Step {i + 1}: hold time must be 0–9999 s.");
                p.Steps.Add(new ProfileStep(r.Ramp.Value ?? 1, r.Target.Value ?? 0, (int)Math.Round(r.Hold.Value ?? 0)));
            }
            if (_useHoldback.Checked) {
                if (!_holdback.IsValid) errors?.Add("Holdback must be 0–100 °C.");
                p.Holdback = _holdback.Value;
            }
            if (errors != null)
                foreach (var e in p.Validate(State.Settings.MaxTemperature))
                    if (!errors.Contains(e) && !e.Contains("ramp rate must be greater")) errors.Add(e);
            return p;
        }

        private void OnUseChanged(int idx) {
            if (_loading) return;
            _loading = true;
            bool on = _rows[idx].Use.Checked;
            if (idx == 0 && !on) _rows[0].Use.Checked = true; // at least one step
            else if (on) for (int i = 0; i < idx; i++) _rows[i].Use.Checked = true;
            else for (int i = idx + 1; i < _rows.Count; i++) _rows[i].Use.Checked = false;
            _loading = false;
            ApplyRowStates();
            MarkDirty();
        }

        private void ApplyRowStates() {
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                bool on = r.Use.Checked;
                r.Ramp.Enabled = r.Target.Enabled = r.Hold.Enabled = on;
                r.Number.ForeColor = on ? Theme.Text : Theme.TextFaint;
                r.Use.Enabled = i > 0;
            }
        }

        private void MarkDirty() {
            if (_loading) return;
            if (!_dirty) { _dirty = true; UpdateHeader(); }
            SchedulePreview();
        }

        private void UpdateHeader() {
            string file = string.IsNullOrEmpty(_filePath) ? "Not saved yet" : Path.GetFileName(_filePath);
            _editorHeader.Subtitle = file + (_dirty ? "  ·  modified" : "");
        }

        private void SchedulePreview() {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview() {
            var errors = new List<string>();
            var p = BuildProfile(errors);
            double start = _startTemp.Value ?? 25;
            var c = ProfileMath.BuildPlannedCurve(p, start);
            _curve.Points.Clear();
            foreach (var pt in c.Points) _curve.Points.Add(new PointD(pt.Time, pt.Temp));
            _chart.Markers.Clear();
            for (int i = 0; i < c.StepStarts.Count; i++)
                _chart.Markers.Add(new ChartMarker { X = c.StepStarts[i], Label = "S" + (i + 1) });
            _chart.XMin = 0;
            _chart.XMax = Math.Max(60, c.TotalSeconds * 1.04);
            _chart.MinXSpan = 60;
            _chart.LimitLine = State.Settings.MaxTemperature;
            _chart.LimitLabel = $"Limit {State.Settings.MaxTemperature:0} °C";
            _chart.Invalidate();

            double rampTime = 0, temp = start;
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                if (i < p.Steps.Count) {
                    var s = p.Steps[i];
                    double ramp = s.RampRate > 0 ? Math.Abs(s.Target - temp) / s.RampRate : 0;
                    rampTime += ramp;
                    temp = s.Target;
                    r.Time.Text = ProfileMath.FormatDuration(ramp + s.HoldSeconds);
                } else r.Time.Text = "";
            }
            _statTotal.Value = ProfileMath.FormatDuration(c.TotalSeconds);
            _statPeak.Value = $"{c.PeakTemp:0.#} °C";
            _statRamp.Value = ProfileMath.FormatDuration(rampTime);
            _statSteps.Value = $"{p.Steps.Count} of 8";
            _validation.Text = string.Join(Environment.NewLine, errors.Distinct().Take(4));
            UpdateButtons();
        }

        private void UpdateButtons() {
            bool conn = State.Device.IsConnected, busy = State.Busy;
            _read.Enabled = conn && !busy;
            _write.Enabled = conn && !busy;
            _slot.Enabled = !busy;
        }

        // ==================================================================== file commands

        private bool ConfirmDiscard() {
            if (!_dirty) return true;
            int r = DarkDialog.Show(FindForm(), "Unsaved changes", "The current profile has changes that are not saved to a file.",
                DialogTone.Warning, ["Save", "Discard", "Cancel"], 0);
            if (r == 0) return SaveProfile(false);
            return r == 1;
        }

        private void ShowNewMenu() {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = Theme.Body };
            foreach (var preset in ProfilePresets.All) {
                var pr = preset;
                var item = new ToolStripMenuItem(pr.Title) { Padding = new Padding(0, Theme.S(3), 0, Theme.S(3)) };
                item.Click += (s, e) => {
                    if (!ConfirmDiscard()) return;
                    LoadProfile(pr.Create(), "");
                    _dirty = true;
                    UpdateHeader();
                };
                menu.Items.Add(item);
            }
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(_new, new Point(0, _new.Height + Theme.S(2)));
        }

        private void OpenProfile() {
            if (!ConfirmDiscard()) return;
            using var d = new OpenFileDialog { Filter = ReflowProfile.FileFilter, InitialDirectory = EnsureProfilesFolder() };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                var p = ReflowProfile.Load(d.FileName);
                LoadProfile(p, d.FileName);
                State.Settings.LastProfilePath = d.FileName;
                State.SetStatus("Opened " + Path.GetFileName(d.FileName), StatusKind.Success);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Cannot open profile", ex.InnerException?.Message ?? ex.Message);
            }
        }

        private bool SaveProfile(bool saveAs) {
            var p = BuildProfile(null);
            string path = _filePath;
            if (saveAs || string.IsNullOrEmpty(path)) {
                using var d = new SaveFileDialog {
                    Filter = ReflowProfile.SaveFilter,
                    InitialDirectory = string.IsNullOrEmpty(_filePath) ? EnsureProfilesFolder() : Path.GetDirectoryName(_filePath),
                    FileName = SafeName(p.Name) + ReflowProfile.FileExtension,
                    DefaultExt = ReflowProfile.FileExtension,
                };
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                path = d.FileName;
            }
            try {
                p.Save(path);
                _filePath = path;
                _dirty = false;
                State.Settings.LastProfilePath = path;
                UpdateHeader();
                State.SetStatus("Saved " + Path.GetFileName(path), StatusKind.Success);
                return true;
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Cannot save profile", ex.Message);
                return false;
            }
        }

        private static string EnsureProfilesFolder() {
            try { Directory.CreateDirectory(AppSettings.ProfilesFolder); } catch { }
            return AppSettings.ProfilesFolder;
        }

        private static string SafeName(string s) {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "profile" : s.Trim();
        }

        // ==================================================================== controller transfer

        private async System.Threading.Tasks.Task ReadFromController() {
            int slot = _slot.SelectedIndex;
            if (slot < 0 || !ConfirmDiscard()) return;
            var (ok, p) = await State.RunAsync($"Reading pattern {slot}", (d, progress, ct) => d.DownloadProfile(slot, progress, ct));
            if (!ok) return;
            var known = State.SlotProfiles[slot];
            if (known != null && SameSteps(known, p)) { p.Name = known.Name; p.Notes = known.Notes; }
            State.SetSlotProfile(slot, p);
            State.Device.NotePattern(slot);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Pattern {slot} profile";
            LoadProfile(p, "");
            _dirty = true;
            UpdateHeader();
            State.SetStatus($"Read pattern {slot}: {p.Steps.Count} step(s)", StatusKind.Success);
        }

        private static bool SameSteps(ReflowProfile a, ReflowProfile b) {
            if (a.Steps.Count != b.Steps.Count) return false;
            for (int i = 0; i < a.Steps.Count; i++)
                if (Math.Abs(a.Steps[i].RampRate - b.Steps[i].RampRate) > 0.011 || Math.Abs(a.Steps[i].Target - b.Steps[i].Target) > 0.11 || a.Steps[i].HoldSeconds != b.Steps[i].HoldSeconds)
                    return false;
            return true;
        }

        private async System.Threading.Tasks.Task WriteToController() {
            int slot = _slot.SelectedIndex;
            if (slot < 0) return;
            var errors = new List<string>();
            var p = BuildProfile(errors);
            if (errors.Count > 0) {
                DarkDialog.Error(FindForm(), "Fix the profile first", string.Join(Environment.NewLine, errors.Distinct()));
                return;
            }
            var curve = ProfileMath.BuildPlannedCurve(p, State.Settings.PreviewStartTemperature);
            if (!DarkDialog.Confirm(FindForm(), $"Write to pattern {slot}?",
                $"\"{p.Name}\" ({p.Steps.Count} step(s), peak {curve.PeakTemp:0.#} °C, about {ProfileMath.FormatDuration(curve.TotalSeconds)}) will replace whatever is stored in controller pattern {slot}.",
                "Write", "Cancel"))
                return;

            var mode = State.Settings.UnusedSteps;
            bool verify = State.Settings.VerifyAfterWrite;
            var (ok, res) = await State.RunAsync($"Writing pattern {slot}", (d, progress, ct) => d.UploadProfile(p, slot, mode, verify, progress, ct));
            if (!ok) return;
            State.SetSlotProfile(slot, p);
            State.Device.NotePattern(slot);
            if (res.Warnings.Count > 0)
                DarkDialog.Show(FindForm(), "Written with warnings",
                    "The controller accepted the profile but some values read back differently (the controller may round them):\n\n" + string.Join("\n", res.Warnings.Take(12)),
                    DialogTone.Warning, ["OK"]);
            else
                State.SetStatus($"Pattern {slot} written" + (verify ? " and verified" : ""), StatusKind.Success);
        }

        public override bool HandleShortcut(Keys keys) {
            switch (keys) {
                case Keys.Control | Keys.S: SaveProfile(false); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveProfile(true); return true;
                case Keys.Control | Keys.O: OpenProfile(); return true;
                case Keys.Control | Keys.N: ShowNewMenu(); return true;
            }
            return false;
        }

        public bool ConfirmClose() => ConfirmDiscard();

        public override void OnShown() => UpdatePreview();
    }
}
