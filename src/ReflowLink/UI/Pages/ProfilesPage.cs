// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            public ToggleSwitch Use, WaitForBed, WaitForIr;
            public NumberBox Ramp, Target, BedTarget, Hold;
            public ThemedLabel Time;
        }

        private readonly List<StepRow> _rows = [];
        private readonly ModernTextBox _name;
        private readonly ModernDropDown _slot;
        private readonly ModernButton _read, _write, _new, _open, _save, _saveAs, _readHoldback, _writeHoldback, _readBedHoldback, _writeBedHoldback;
        private readonly NumberBox _holdback, _bedHoldback, _startTemp;
        private readonly ThemedLabel _validation;
        private readonly CardHeader _editorHeader;
        private readonly ChartView _chart;
        private readonly ChartSeries _curve, _bedCurve;
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
            tip.SetToolTip(_read, "Read the selected pattern into the editor (from both controllers when bed heat control is on)");
            tip.SetToolTip(_write, "Store the edited profile in the selected pattern (of both controllers when bed heat control is on)");

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
            var grid = Table(9, Pc410Protocol.SegmentCount + 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            int[] widths = [26, 46, 100, 100, 100, 86, 60, 60, 54];
            foreach (var w in widths) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(w)));
            string[] heads = ["#", "Use", "Ramp", "IR Target", "Bed Heat Target", "IR Hold", "Wait for Bed", "Wait for IR", "Time"];
            for (int c = 0; c < heads.Length; c++) {
                var h = new ThemedLabel(heads[c].ToUpperInvariant(), Theme.SmallBold, Theme.TextFaint) {
                    AutoSize = false,
                    Width = Theme.S(widths[c] - (c is >= 2 and <= 5 ? 12 : 2)),
                    Height = Theme.SmallBold.Height * 2 + Theme.S(4),
                    TextAlign = c is >= 2 and <= 5 ? ContentAlignment.BottomRight : ContentAlignment.BottomLeft,
                    Margin = new Padding(Theme.S(2), 0, 0, Theme.S(6)),
                };
                grid.Controls.Add(h, c, 0);
            }
            tip.SetToolTip(grid.GetControlFromPosition(6, 0), "Hold the top IR while the bed is behind during this step");
            tip.SetToolTip(grid.GetControlFromPosition(7, 0), "Hold the bed while the top IR is behind during this step");
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < Pc410Protocol.SegmentCount; i++) {
                int idx = i;
                NumberBox Box(double min, double max, int decimals, double step, string suffix, int width) =>
                    new(min, max, decimals, step) { Suffix = suffix, Width = Theme.S(width), Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) };
                var r = new StepRow {
                    Number = new ThemedLabel((i + 1).ToString(), Theme.BodyBold, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                    Use = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = Padding.Empty },
                    Ramp = Box(0.01, 10, 2, 0.1, "°C/s", 92),
                    Target = Box(0, 400, 1, 1, "°C", 92),
                    BedTarget = Box(0, 400, 1, 1, "°C", 92),
                    Hold = Box(0, 9999, 0, 5, "s", 78),
                    WaitForBed = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(6), 0, 0, 0) },
                    WaitForIr = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(6), 0, 0, 0) },
                    Time = new ThemedLabel("", Theme.Small, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                };
                r.Use.CheckedChanged += (s, e) => OnUseChanged(idx);
                r.Ramp.TextChanged += (s, e) => MarkDirty();
                r.Target.TextChanged += (s, e) => MarkDirty();
                r.BedTarget.TextChanged += (s, e) => MarkDirty();
                r.Hold.TextChanged += (s, e) => MarkDirty();
                r.WaitForBed.CheckedChanged += (s, e) => MarkDirty();
                r.WaitForIr.CheckedChanged += (s, e) => MarkDirty();
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(r.Number, 0, i + 1);
                grid.Controls.Add(r.Use, 1, i + 1);
                grid.Controls.Add(r.Ramp, 2, i + 1);
                grid.Controls.Add(r.Target, 3, i + 1);
                grid.Controls.Add(r.BedTarget, 4, i + 1);
                grid.Controls.Add(r.Hold, 5, i + 1);
                grid.Controls.Add(r.WaitForBed, 6, i + 1);
                grid.Controls.Add(r.WaitForIr, 7, i + 1);
                grid.Controls.Add(r.Time, 8, i + 1);
                _rows.Add(r);
            }

            // Holdback + validation below the grid
            var extras = Table(1, 0);
            extras.Dock = DockStyle.Top;
            extras.AutoSize = true;
            extras.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            extras.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            FlowLayoutPanel HoldbackRow(string label, out NumberBox box, out ModernButton read, out ModernButton write, bool bed) {
                var row = Flow();
                var text = new ThemedLabel(label, Theme.Body, Theme.Text) { AutoSize = false, Width = Theme.S(210), Height = Theme.S(22), Margin = new Padding(0, Theme.S(8), Theme.S(8), 0) };
                box = new NumberBox(ReflowProfile.HoldbackMin, ReflowProfile.HoldbackMax, 1, 0.5) {
                    Suffix = "°C",
                    Width = Theme.S(100),
                    Margin = new Padding(0, 0, Theme.S(8), Theme.S(6)),
                    Value = ReflowProfile.HoldbackDefault
                };
                box.TextChanged += (s, e) => MarkDirty();
                read = new ModernButton("Read", Glyph.Download) { Margin = new Padding(0, 0, Theme.S(6), 0) };
                write = new ModernButton("Write", Glyph.Upload) { Margin = Padding.Empty };
                string who = bed ? "Bed Heat" : "Top IR";
                tip.SetToolTip(read, $"Read the {who} controller's holdback into the editor");
                tip.SetToolTip(write, $"Write only the holdback to the {who} controller (applies to all its patterns)");
                row.Controls.AddRange([text, box, read, write]);
                return row;
            }
            var hbRow = HoldbackRow("Set Global IR Holdback", out _holdback, out _readHoldback, out _writeHoldback, false);
            _readHoldback.Click += async (s, e) => await ReadHoldback(State.Ir);
            _writeHoldback.Click += async (s, e) => await WriteHoldback(State.Ir);
            var bedHbRow = HoldbackRow("Set Global Bed Heat Holdback", out _bedHoldback, out _readBedHoldback, out _writeBedHoldback, true);
            _readBedHoldback.Click += async (s, e) => await ReadHoldback(State.BedChannel);
            _writeBedHoldback.Click += async (s, e) => await WriteHoldback(State.BedChannel);
            var hbHint = new ThemedLabel(
                "Holdback pauses a controller's program while its temperature lags the setpoint by more than this band. Each controller has one holdback "
                + $"for all its patterns, and it's written with every profile. It can't be switched off; {ReflowProfile.HoldbackMax:0} °C means it practically never triggers. "
                + "Wait for Bed / Wait for IR use the same bands to decide when a heater is behind.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(620), 0), Margin = new Padding(0, Theme.S(6), 0, 0) };
            _validation = new ThemedLabel("", Theme.Small, Theme.Red) { AutoSize = true, MaximumSize = new Size(Theme.S(620), 0), Margin = new Padding(0, Theme.S(10), 0, 0) };
            extras.Controls.Add(new Divider { Dock = DockStyle.None, Width = Theme.S(626), Margin = new Padding(0, Theme.S(6), 0, Theme.S(6)) }, 0, 0);
            extras.Controls.Add(hbRow, 0, 1);
            extras.Controls.Add(bedHbRow, 0, 2);
            extras.Controls.Add(hbHint, 0, 3);
            extras.Controls.Add(_validation, 0, 4);

            var nameRow = Table(2, 1);
            nameRow.Dock = DockStyle.Top;
            nameRow.Height = Theme.S(34 + 18);
            nameRow.Padding = new Padding(0, 0, 0, Theme.S(18));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(82)));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(544)));
            nameRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new ThemedLabel("Name", Theme.Body, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) }, 0, 0);
            nameRow.Controls.Add(_name, 1, 0);

            editor.Controls.Add(extras);
            editor.Controls.Add(grid);
            editor.Controls.Add(nameRow);
            editor.Controls.Add(_editorHeader);
            Theme.ApplyDarkScrollbars(editor);

            // ---------------------------------------------------------------- preview card
            _curve = new ChartSeries { Name = "Top IR", Color = Theme.Accent, Width = 2.2f, FillArea = true };
            _bedCurve = new ChartSeries { Name = "Bed heat", Color = Theme.Green, Width = 1.8f };
            _chart = new ChartView { Dock = DockStyle.Fill, ShowSecondaryAxis = false, EmptyText = "Add a step to see the curve", ShowLegend = false };
            _chart.Series.Add(_curve);
            _chart.Series.Add(_bedCurve);

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
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(690)));
            // On narrow windows the editor gives up width (and scrolls sideways) so the preview stays usable.
            main.Resize += (s, e) => {
                int width = Math.Max(Theme.S(420), Math.Min(Theme.S(690), (int)(main.Width * 0.62)));
                if (Math.Abs(main.ColumnStyles[0].Width - width) > 1) main.ColumnStyles[0].Width = width;
            };
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
            State.Device.StateChanged += (s, e) => UpdatePreview(); // also refreshes the buttons
            State.Bed.StateChanged += (s, e) => UpdatePreview();
            State.BedEnabledChanged += () => { ApplyRowStates(); UpdatePreview(); };

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
                r.BedTarget.Value = src.BedTarget;
                r.Hold.Value = src.HoldSeconds;
                r.WaitForBed.Checked = s?.WaitForBed ?? false;
                r.WaitForIr.Checked = s?.WaitForIr ?? false;
            }
            _holdback.Value = p.Holdback ?? ReflowProfile.HoldbackDefault;
            _bedHoldback.Value = p.BedHoldback ?? ReflowProfile.HoldbackDefault;
            _filePath = path ?? "";
            _loading = false;
            ApplyRowStates();
            _dirty = false;
            UpdateHeader();
            UpdatePreview();
        }

        private ProfileStep DefaultStepAfter(ReflowProfile p, int index) {
            var last = p.Steps.Count > 0 ? p.Steps[p.Steps.Count - 1] : null;
            int ahead = index - p.Steps.Count + 1;
            double target = last != null ? Math.Min(last.Target + 10 * ahead, State.Settings.MaxTemperature) : 150;
            double bed = last != null ? Math.Min(last.BedTarget + 10 * ahead, State.Settings.Bed.MaxTemperature) : 150;
            return new ProfileStep(1.0, target, 20) { BedTarget = bed };
        }

        private ReflowProfile BuildProfile(List<string> errors) {
            var p = new ReflowProfile { Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled profile" : _name.Text.Trim(), Notes = _notes };
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                if (!r.Use.Checked) break;
                if (!r.Ramp.IsValid) errors?.Add($"Step {i + 1}: ramp rate must be between 0.01 and 10 °C/s.");
                if (!r.Target.IsValid) errors?.Add($"Step {i + 1}: enter a valid target temperature.");
                if (!r.Hold.IsValid) errors?.Add($"Step {i + 1}: hold time must be 0–9999 s.");
                bool bedValid = r.BedTarget.IsValid;
                if (State.BedEnabled && !bedValid) errors?.Add($"Step {i + 1}: enter a valid bed heat target.");
                p.Steps.Add(new ProfileStep(r.Ramp.Value ?? 1, r.Target.Value ?? 0, (int)Math.Round(r.Hold.Value ?? 0)) {
                    BedTarget = bedValid ? r.BedTarget.Value.Value : r.Target.Value ?? 0,
                    WaitForBed = r.WaitForBed.Checked,
                    WaitForIr = r.WaitForIr.Checked,
                });
            }
            // Always set: every profile write also writes the (global) holdbacks.
            if (!_holdback.IsValid) errors?.Add(ReflowProfile.HoldbackRangeMessage);
            p.Holdback = _holdback.Value ?? ReflowProfile.HoldbackDefault;
            if (State.BedEnabled && !_bedHoldback.IsValid) errors?.Add($"Bed holdback must be between {ReflowProfile.HoldbackMin:0} and {ReflowProfile.HoldbackMax:0} °C.");
            p.BedHoldback = _bedHoldback.IsValid ? _bedHoldback.Value.Value : ReflowProfile.HoldbackDefault;
            if (errors != null) {
                foreach (var e in p.Validate(State.Settings.MaxTemperature, State.BedEnabled ? State.Settings.Bed.MaxTemperature : null))
                    if (!errors.Contains(e) && !e.Contains("ramp rate must be greater")) errors.Add(e);
                // When connected, also check against the controllers' own setpoint limits (HS / LS).
                if (State.Device.IsConnected) errors.AddRange(Pc410Device.CheckTargets(p, State.Device.ControllerLimits));
                if (State.DualBed && State.Bed.IsConnected)
                    errors.AddRange(Pc410Device.CheckTargets(p.ForBed(), State.Bed.ControllerLimits).Select(e => "Bed: " + e.Replace("target", "bed target")));
            }
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
                r.BedTarget.Enabled = r.WaitForBed.Enabled = on && State.BedEnabled;
                // A single PC410's bed has no program to hold: it simply follows the IR program.
                r.WaitForIr.Enabled = on && State.DualBed;
                r.Number.ForeColor = on ? Theme.Text : Theme.TextFaint;
                r.Use.Enabled = i > 0;
            }
            _bedHoldback.Enabled = State.BedEnabled;
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
            bool bed = State.BedEnabled;
            var bc = ProfileMath.BuildBedCurve(p, start, start, State.SingleBed);
            _bedCurve.Points.Clear();
            if (bed) foreach (var pt in bc.Points) _bedCurve.Points.Add(new PointD(pt.Time, pt.Temp));
            _bedCurve.Visible = bed;
            _chart.ShowLegend = bed;
            _chart.Markers.Clear();
            for (int i = 0; i < c.StepStarts.Count; i++)
                _chart.Markers.Add(new ChartMarker { X = c.StepStarts[i], Label = "S" + (i + 1) });
            _chart.XMin = 0;
            double total = bed ? Math.Max(c.TotalSeconds, bc.TotalSeconds) : c.TotalSeconds;
            _chart.XMax = Math.Max(60, total * 1.04);
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
            // On a second PC410 the bed runs the same ramp rates and reaches its targets at other times (Wait for Bed / Wait
            // for IR line them up); with a single PC410 it follows the IR program.
            _statTotal.Value = ProfileMath.FormatDuration(total);
            _statPeak.Text = "Peak";
            _statPeak.Value = bed ? $"{c.PeakTemp:0}/{bc.PeakTemp:0}" : $"{c.PeakTemp:0.#} °C";
            _statPeak.Invalidate();
            _statRamp.Value = ProfileMath.FormatDuration(rampTime);
            _statSteps.Value = $"{p.Steps.Count} of 8";
            _validation.Text = string.Join(Environment.NewLine, errors.Distinct().Take(4));
            UpdateButtons();
        }

        private void UpdateButtons() {
            bool conn = State.Device.IsConnected, busy = State.Busy;
            bool bedConn = State.DualBed && State.Bed.IsConnected;
            // With a second PC410, patterns are read and written on both controllers.
            bool pair = conn && (!State.DualBed || bedConn);
            _read.Enabled = pair && !busy;
            _write.Enabled = pair && !busy;
            _readHoldback.Enabled = conn && !busy;
            _writeHoldback.Enabled = conn && !busy;
            _readBedHoldback.Enabled = bedConn && !busy;
            _writeBedHoldback.Enabled = bedConn && !busy;
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

        private async Task ReadFromController() {
            int slot = _slot.SelectedIndex;
            if (slot < 0 || !ConfirmDiscard()) return;
            bool both = State.DualBed, single = State.SingleBed;
            var (ok, read) = await State.RunPairAsync(both ? $"Reading pattern {slot} from both controllers" : $"Reading pattern {slot}",
                (ir, bed, progress, ct) => PatternPair.Read(ir, bed, slot, progress, ct));
            if (!ok) return;
            var p = read.Profile;
            // The controllers don't store Wait for Bed / Wait for IR (nor, with a single PC410, the bed targets): use what
            // ReflowLink last wrote to this pattern.
            var sync = State.Settings.SyncFor(slot);
            bool bedRestored = sync?.ApplyTo(p, single) == true;
            if (single && !bedRestored) sync?.ApplyTo(p);
            var known = State.SlotProfiles[slot];
            if (known != null && SameSteps(known, p)) { p.Name = known.Name; p.Notes = known.Notes; }
            State.SetSlotProfile(slot, p);
            State.Device.NotePattern(slot);
            if (both) State.Bed.NotePattern(slot);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Pattern {slot} profile";
            LoadProfile(p, "");
            _dirty = true;
            UpdateHeader();
            if (read.Warnings.Count > 0)
                DarkDialog.Show(FindForm(), "The two patterns don't match",
                    $"Pattern {slot} was read, but the bed controller's copy differs from the IR controller's:\n\n" + string.Join("\n", read.Warnings.Take(10))
                    + "\n\nWrite the profile again to make both controllers run the same steps.",
                    DialogTone.Warning, ["OK"]);
            else if (single && !bedRestored)
                State.SetStatus($"Read pattern {slot}: {p.Steps.Count} step(s). No bed targets saved for it: they're set to the IR targets, so check them and write the profile again.", StatusKind.Warning);
            else
                State.SetStatus($"Read pattern {slot}: {p.Steps.Count} step(s)" + (single ? ", bed targets from ReflowLink" : ""), StatusKind.Success);
        }

        private static bool SameSteps(ReflowProfile a, ReflowProfile b) {
            if (a.Steps.Count != b.Steps.Count) return false;
            for (int i = 0; i < a.Steps.Count; i++)
                if (Math.Abs(a.Steps[i].RampRate - b.Steps[i].RampRate) > 0.011 || Math.Abs(a.Steps[i].Target - b.Steps[i].Target) > 0.11 || a.Steps[i].HoldSeconds != b.Steps[i].HoldSeconds)
                    return false;
            return true;
        }

        private async Task WriteToController() {
            int slot = _slot.SelectedIndex;
            if (slot < 0) return;
            var errors = new List<string>();
            var p = BuildProfile(errors);
            if (errors.Count > 0) {
                DarkDialog.Error(FindForm(), "Fix the profile first", string.Join(Environment.NewLine, errors.Distinct()));
                return;
            }
            bool both = State.DualBed, single = State.SingleBed;
            var curve = ProfileMath.BuildPlannedCurve(p, State.Settings.PreviewStartTemperature);
            string where = both ? $"pattern {slot} of both the Top IR and the Bed Heat controller"
                : single ? $"controller pattern {slot} (ReflowLink keeps the bed targets for it)"
                : $"controller pattern {slot}";
            if (!DarkDialog.Confirm(FindForm(), $"Write to pattern {slot}?",
                $"\"{p.Name}\" ({p.Steps.Count} step(s), peak {curve.PeakTemp:0.#} °C, about {ProfileMath.FormatDuration(curve.TotalSeconds)}) will replace whatever is stored in {where}.",
                "Write", "Cancel"))
                return;

            var mode = State.Settings.UnusedSteps;
            bool verify = State.Settings.VerifyAfterWrite;
            var bedMode = State.Settings.Bed.UnusedSteps;
            bool bedVerify = State.Settings.Bed.VerifyAfterWrite;
            var (ok, res) = await State.RunPairAsync($"Writing pattern {slot}",
                (ir, bed, progress, ct) => PatternPair.Write(ir, bed, p, slot, mode, verify, progress, ct, bedMode, bedVerify));
            if (!ok) return;
            State.UnblockSlot(slot);
            State.SetSlotProfile(slot, p);
            State.Settings.SetSync(slot, p, State.BedEnabled);
            State.Settings.Save();
            State.Device.NotePattern(slot);
            if (both) State.Bed.NotePattern(slot);
            if (res.Warnings.Count > 0)
                DarkDialog.Show(FindForm(), "Written with warnings",
                    "The controllers accepted the profile but some values read back differently (they may round them):\n\n" + string.Join("\n", res.Warnings.Take(12)),
                    DialogTone.Warning, ["OK"]);
            else
                State.SetStatus($"Pattern {slot} written" + (both ? " to both controllers" : "") + (verify && (!both || bedVerify) ? " and verified" : "")
                    + (single ? "; bed targets saved in ReflowLink" : ""), StatusKind.Success);
        }

        private async Task ReadHoldback(ControllerChannel channel) {
            var box = channel.IsBed ? _bedHoldback : _holdback;
            var (ok, value) = await State.RunOnAsync(channel, $"Reading {channel.Name} holdback", (d, progress, ct) => d.ReadHoldback());
            if (!ok) return;
            box.Value = value;
            State.SetStatus($"{channel.Name} holdback is {value:0.#} °C", StatusKind.Success);
        }

        private async Task WriteHoldback(ControllerChannel channel) {
            var box = channel.IsBed ? _bedHoldback : _holdback;
            if (!box.IsValid || box.Value is not double value) {
                DarkDialog.Error(FindForm(), "Check the holdback", ReflowProfile.HoldbackRangeMessage);
                return;
            }
            var (ok, stored) = await State.RunOnAsync(channel, $"Writing {channel.Name} holdback", (d, progress, ct) => d.WriteHoldback(value));
            if (ok) State.SetStatus($"{channel.Name} holdback set to {stored:0.#} °C for all its patterns", StatusKind.Success);
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
