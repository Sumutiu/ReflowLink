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

        /// <summary>Shows the value in the warning colour.</summary>
        public bool Warn { get => _warn; set { _warn = value; Invalidate(); } }
        private bool _warn;

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            Theme.HighQuality(g);
            using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Sf(8)))
            using (var b = new SolidBrush(Theme.SurfaceRaised))
                g.FillPath(b, p);
            int pad = Theme.S(12);
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.SmallBold, new Rectangle(pad, Theme.S(7), Width - pad * 2, Theme.S(18)), Theme.TextFaint, Theme.LeftMiddle);
            TextRenderer.DrawText(g, _value, Theme.Heading, new Rectangle(pad, Theme.S(24), Width - pad * 2, Theme.S(24)), Warn ? Theme.Yellow : Theme.Text, Theme.LeftMiddle);
        }
    }

    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class ProfilesPage : PageBase {
        private sealed class StepRow {
            public ThemedLabel Number;
            public ToggleSwitch Use, WaitForBed, WaitForIr;
            public NumberBox Ramp, BedRamp, Target, BedTarget, Hold;
            public ThemedLabel Time;
        }

        private readonly List<StepRow> _rows = [];
        private readonly ModernTextBox _name;
        private readonly ModernDropDown _slot;
        private readonly ModernButton _read, _write, _new, _open, _save, _saveAs, _readHoldback, _writeHoldback, _readBedHoldback, _writeBedHoldback,
            _patterns, _notesButton, _overlay;
        private readonly NumberBox _holdback, _bedHoldback, _startTemp, _constTarget, _constBed, _constRamp, _constBedRamp;
        private readonly ToggleSwitch _constant;
        private readonly ThemedLabel _constBedLabel, _constBedAt;
        private readonly ToolTip _tip;
        private readonly ThemedLabel _validation;
        private readonly CardHeader _editorHeader;
        private readonly ChartView _chart;
        private readonly ChartSeries _curve, _bedCurve, _overlayIr, _overlayBed;
        private readonly MiniStat _statTotal, _statPeak, _statSteps, _statRamp, _statAbove, _statSteepest;
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
            _patterns = new ModernButton("Patterns…", Glyph.None) { Margin = new Padding(0, 0, Theme.S(12), 0) };
            _patterns.Click += async (s, e) => await ShowPatterns();
            var tip = _tip = Theme.CreateToolTip();
            tip.SetToolTip(_patterns, "All ten controller patterns: read them all, compare one with the editor, or write a file to one");
            tip.SetToolTip(_read, "Read the selected pattern into the editor (from both controllers when bed heat control is on)");
            tip.SetToolTip(_write, "Store the edited profile in the selected pattern (of both controllers when bed heat control is on)");

            var right = Flow();
            right.Controls.AddRange([_patterns, _slot, _read, _write]);

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
            var grid = Table(10, Pc410Protocol.SegmentCount + 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            int[] widths = [24, 44, 84, 84, 78, 78, 70, 54, 54, 48];
            foreach (var w in widths) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(w)));
            string[] heads = ["#", "Use", "IR Ramp", "Bed Ramp", "IR Target", "Bed Heat Target", "Hold Time", "Wait for Bed", "Wait for IR", "Time"];
            for (int c = 0; c < heads.Length; c++) {
                var h = new ThemedLabel(heads[c].ToUpperInvariant(), Theme.SmallBold, Theme.TextFaint) {
                    AutoSize = false,
                    Width = Theme.S(widths[c] - (c is >= 2 and <= 6 ? 10 : 2)),
                    Height = Theme.SmallBold.Height * 2 + Theme.S(4),
                    TextAlign = c is >= 2 and <= 6 ? ContentAlignment.BottomRight : ContentAlignment.BottomLeft,
                    Margin = new Padding(Theme.S(2), 0, 0, Theme.S(6)),
                };
                grid.Controls.Add(h, c, 0);
            }
            tip.SetToolTip(grid.GetControlFromPosition(2, 0), "How fast the top IR heats (or cools) to this step's target");
            tip.SetToolTip(grid.GetControlFromPosition(3, 0), "How fast the bed heats (or cools) to this step's bed target");
            tip.SetToolTip(grid.GetControlFromPosition(6, 0), "How long both heaters stay at the step's set temperatures");
            tip.SetToolTip(grid.GetControlFromPosition(7, 0), "When the top IR reaches its set temperature first, it waits there for the bed; then both do the hold time together");
            tip.SetToolTip(grid.GetControlFromPosition(8, 0), "When the bed reaches its set temperature first, it waits there for the top IR; then both do the hold time together");
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < Pc410Protocol.SegmentCount; i++) {
                int idx = i;
                NumberBox Box(double min, double max, int decimals, double step, string suffix, int width) =>
                    new(min, max, decimals, step) { Suffix = suffix, Width = Theme.S(width), Anchor = AnchorStyles.Left, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)) };
                var r = new StepRow {
                    Number = new ThemedLabel((i + 1).ToString(), Theme.BodyBold, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                    Use = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = Padding.Empty },
                    Ramp = Box(0.01, 10, 2, 0.1, "°C/s", 78),
                    BedRamp = Box(0.01, 10, 2, 0.1, "°C/s", 78),
                    Target = Box(0, 400, 1, 1, "°C", 72),
                    BedTarget = Box(0, 400, 1, 1, "°C", 72),
                    Hold = Box(0, 9999, 0, 5, "s", 64),
                    WaitForBed = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(6), 0, 0, 0) },
                    WaitForIr = new ToggleSwitch() { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(6), 0, 0, 0) },
                    Time = new ThemedLabel("", Theme.Small, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) },
                };
                r.Use.CheckedChanged += (s, e) => OnUseChanged(idx);
                r.Ramp.TextChanged += (s, e) => MarkDirty();
                r.BedRamp.TextChanged += (s, e) => MarkDirty();
                r.Target.TextChanged += (s, e) => MarkDirty();
                r.BedTarget.TextChanged += (s, e) => MarkDirty();
                r.Hold.TextChanged += (s, e) => MarkDirty();
                r.WaitForBed.CheckedChanged += (s, e) => MarkDirty();
                r.WaitForIr.CheckedChanged += (s, e) => MarkDirty();
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(r.Number, 0, i + 1);
                grid.Controls.Add(r.Use, 1, i + 1);
                grid.Controls.Add(r.Ramp, 2, i + 1);
                grid.Controls.Add(r.BedRamp, 3, i + 1);
                grid.Controls.Add(r.Target, 4, i + 1);
                grid.Controls.Add(r.BedTarget, 5, i + 1);
                grid.Controls.Add(r.Hold, 6, i + 1);
                grid.Controls.Add(r.WaitForBed, 7, i + 1);
                grid.Controls.Add(r.WaitForIr, 8, i + 1);
                grid.Controls.Add(r.Time, 9, i + 1);
                _rows.Add(r);
            }

            // Holdback + validation below the grid
            var extras = Table(1, 0);
            extras.Dock = DockStyle.Top;
            extras.AutoSize = true;
            extras.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Room below the last line (the warnings), so it doesn't sit on the card's edge when scrolled to the bottom.
            extras.Padding = new Padding(0, 0, 0, Theme.S(20));
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
                + "Wait for Bed / Wait for IR use the same bands to decide when a heater is at its set temperature.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(620), 0), Margin = new Padding(0, Theme.S(6), 0, 0) };
            _validation = new ThemedLabel("", Theme.Small, Theme.Red) { AutoSize = true, MaximumSize = new Size(Theme.S(620), 0), Margin = new Padding(0, Theme.S(10), 0, 0) };

            // Constant temperature: one temperature until Stop, instead of the steps.
            _constant = new ToggleSwitch("Use Constant Temperature") { Margin = new Padding(0, Theme.S(6), 0, Theme.S(8)) };
            _constant.CheckedChanged += (s, e) => {
                if (_loading) return;
                ApplyRowStates();
                MarkDirty();
            };
            NumberBox ConstantBox(double min, double max, int decimals, double step, string suffix, double value) {
                var box = new NumberBox(min, max, decimals, step) {
                    Suffix = suffix,
                    Width = Theme.S(100),
                    Margin = new Padding(0, 0, Theme.S(10), Theme.S(6)),
                    Value = value,
                    Enabled = false,
                };
                box.TextChanged += (s, e) => MarkDirty();
                return box;
            }
            ThemedLabel ConstantLabel(string text, int width) =>
                new(text, Theme.Body, Theme.TextDim) { AutoSize = false, Width = Theme.S(width), Height = Theme.S(22), Margin = new Padding(0, Theme.S(8), Theme.S(8), 0) };
            _constTarget = ConstantBox(0, 400, 1, 1, "°C", ReflowProfile.DefaultConstantTarget);
            _constRamp = ConstantBox(0.01, 10, 2, 0.1, "°C/s", ReflowProfile.DefaultConstantRamp);
            _constBed = ConstantBox(0, 400, 1, 1, "°C", ReflowProfile.DefaultConstantTarget);
            _constBedRamp = ConstantBox(0.01, 10, 2, 0.1, "°C/s", ReflowProfile.DefaultConstantRamp);
            _constBedLabel = ConstantLabel("Bed Heat", 90);
            _constBedAt = ConstantLabel("ramp", 40);
            var constIrRow = Flow();
            constIrRow.Controls.AddRange([ConstantLabel("Top IR", 90), _constTarget, ConstantLabel("ramp", 40), _constRamp]);
            var constBedRow = Flow();
            constBedRow.Controls.AddRange([_constBedLabel, _constBed, _constBedAt, _constBedRamp]);
            tip.SetToolTip(_constant, "Hold one temperature until you press Stop, instead of running the steps");
            tip.SetToolTip(_constRamp, "How fast the top IR heats up to the constant temperature");
            var constHint = new ThemedLabel(
                "Holds the temperature until you press Stop, for at most about 22 hours. It's written to the controller as a pattern of 8 steps "
                + "at that temperature (the ramp, then the longest hold), because the station only heats while a program runs. "
                + "The steps above are kept in the profile for when you switch this off.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(620), 0), Margin = new Padding(0, Theme.S(2), 0, 0) };

            Divider Line() => new() { Dock = DockStyle.None, Width = Theme.S(626), Margin = new Padding(0, Theme.S(6), 0, Theme.S(6)) };
            extras.Controls.Add(Line(), 0, 0);
            extras.Controls.Add(_constant, 0, 1);
            extras.Controls.Add(constIrRow, 0, 2);
            extras.Controls.Add(constBedRow, 0, 3);
            extras.Controls.Add(constHint, 0, 4);
            extras.Controls.Add(Line(), 0, 5);
            extras.Controls.Add(hbRow, 0, 6);
            extras.Controls.Add(bedHbRow, 0, 7);
            extras.Controls.Add(hbHint, 0, 8);
            extras.Controls.Add(_validation, 0, 9);

            var nameRow = Table(3, 1);
            nameRow.Dock = DockStyle.Top;
            nameRow.Height = Theme.S(34 + 18);
            nameRow.Padding = new Padding(0, 0, 0, Theme.S(18));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(82)));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(444)));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(100)));
            nameRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new ThemedLabel("Name", Theme.Body, Theme.TextDim) { Anchor = AnchorStyles.Left, Margin = new Padding(Theme.S(4), 0, 0, 0) }, 0, 0);
            nameRow.Controls.Add(_name, 1, 0);
            _notesButton = new ModernButton("Notes", Glyph.None) { AutoWidth = false, Width = Theme.S(92), Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            _notesButton.Click += (s, e) => EditNotes();
            tip.SetToolTip(_notesButton, "Notes saved with the profile file");
            nameRow.Controls.Add(_notesButton, 2, 0);

            editor.Controls.Add(extras);
            editor.Controls.Add(grid);
            editor.Controls.Add(nameRow);
            editor.Controls.Add(_editorHeader);
            Theme.ApplyDarkScrollbars(editor);

            // ---------------------------------------------------------------- preview card
            _curve = new ChartSeries { Name = "Top IR", Color = Theme.Accent, Width = 2.2f, FillArea = true };
            _bedCurve = new ChartSeries { Name = "Bed heat", Color = Theme.Green, Width = 1.8f };
            _overlayIr = new ChartSeries { Name = "Recorded IR", Color = Theme.Yellow, Width = 1.4f, ShowInLegend = false };
            _overlayBed = new ChartSeries { Name = "Recorded bed", Color = Color.FromArgb(45, 212, 191), Width = 1.2f, ShowInLegend = false };
            _chart = new ChartView { Dock = DockStyle.Fill, ShowSecondaryAxis = false, EmptyText = "Add a step to see the curve", ShowLegend = false };
            _chart.Series.Add(_overlayBed);
            _chart.Series.Add(_overlayIr);
            _chart.Series.Add(_bedCurve);
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
            _overlay = new ModernButton("Overlay run…", Glyph.None) { Margin = new Padding(0, 0, Theme.S(12), 0) };
            _overlay.Click += (s, e) => ToggleOverlay();
            tip.SetToolTip(_overlay, "Lay a recorded run (CSV) over the preview, to compare what the station did with the plan");
            previewHeader.Tools.Controls.Add(_overlay);
            previewHeader.Tools.Controls.Add(new ThemedLabel("Start at", Theme.Small, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(8), 0) });
            previewHeader.Tools.Controls.Add(_startTemp);

            _statTotal = new MiniStat("Total") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(4), Theme.S(8)) };
            _statPeak = new MiniStat("Peak") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), Theme.S(8)) };
            _statRamp = new MiniStat("Ramping") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, 0, Theme.S(8)) };
            _statAbove = new MiniStat("Above 183 / 217 °C") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(4), 0) };
            _statSteepest = new MiniStat("Steepest ramp") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0) };
            _statSteps = new MiniStat("Steps") { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, 0, 0) };
            tip.SetToolTip(_statAbove, "Time the planned IR curve spends above 183 °C (leaded liquidus) and 217 °C (lead-free liquidus)");
            tip.SetToolTip(_statSteepest, $"Ramps above {ProfileMath.SteepRamp:0} °C/s can crack components and boards");
            var stats = Table(3, 2);
            stats.Dock = DockStyle.Bottom;
            stats.Height = Theme.S(54 * 2 + 8 + 12);
            stats.Padding = new Padding(0, Theme.S(12), 0, 0);
            for (int i = 0; i < 3; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stats.Controls.Add(_statTotal, 0, 0);
            stats.Controls.Add(_statPeak, 1, 0);
            stats.Controls.Add(_statRamp, 2, 0);
            stats.Controls.Add(_statAbove, 0, 1);
            stats.Controls.Add(_statSteepest, 1, 1);
            stats.Controls.Add(_statSteps, 2, 1);

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
            State.StationChanged += () => { ApplyRampDecimals(); UpdatePreview(); };
            State.Device.StateChanged += (s, e) => UpdatePreview(); // also refreshes the buttons
            State.Bed.StateChanged += (s, e) => UpdatePreview();
            State.BedEnabledChanged += () => { ApplyRowStates(); UpdatePreview(); };

            ApplyRampDecimals();
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
                r.BedRamp.Value = src.BedRampRate;
                r.Target.Value = src.Target;
                r.BedTarget.Value = src.BedTarget;
                r.Hold.Value = src.HoldSeconds;
                r.WaitForBed.Checked = s?.WaitForBed ?? false;
                r.WaitForIr.Checked = s?.WaitForIr ?? false;
            }
            _holdback.Value = p.Holdback ?? ReflowProfile.HoldbackDefault;
            _bedHoldback.Value = p.BedHoldback ?? ReflowProfile.HoldbackDefault;
            _constant.Checked = p.ConstantTemperature;
            _constTarget.Value = p.ConstantTarget;
            _constBed.Value = p.ConstantBedTarget;
            _constRamp.Value = p.ConstantRamp;
            _constBedRamp.Value = p.ConstantBedRamp;
            _filePath = path ?? "";
            _loading = false;
            UpdateNotesButton();
            ApplyRowStates();
            _dirty = false;
            UpdateHeader();
            UpdatePreview();
        }

        /// <summary>
        /// A controller counting ramps per minute stores steps of 1/60 °C/s, so the editor shows 4 decimals then (0.0167
        /// °C/s is 1 °C/min); otherwise 2. The bed ramps follow the bed controller's setting (a single PC410's bed is
        /// ramped by ReflowLink: 2 decimals).
        /// </summary>
        private void ApplyRampDecimals() {
            bool irPerMinute = State.Settings.Units.Ramp == RampUnit.PerMinute;
            bool bedPerMinute = State.DualBed && State.Settings.Bed.Units.Ramp == RampUnit.PerMinute;
            bool wasLoading = _loading;
            _loading = true;
            foreach (var box in _rows.Select(r => r.Ramp).Append(_constRamp)) SetRampDecimals(box, irPerMinute);
            foreach (var box in _rows.Select(r => r.BedRamp).Append(_constBedRamp)) SetRampDecimals(box, bedPerMinute);
            _loading = wasLoading;
        }

        private static void SetRampDecimals(NumberBox box, bool perMinute) {
            int decimals = perMinute ? 4 : 2;
            if (box.Decimals == decimals) return;
            var v = box.Value;
            box.Decimals = decimals;
            box.Step = perMinute ? 1 / 60.0 : 0.1;
            if (v.HasValue) box.Value = Math.Round(v.Value, decimals);
        }

        /// <summary>Fields that don't hold a usable number (saving them would store a made-up value).</summary>
        private List<string> FieldErrors() {
            var errors = new List<string>();
            if (_constant.Checked) {
                if (!_constTarget.IsValid) errors.Add("Enter a valid constant temperature for the Top IR.");
                if (!_constRamp.IsValid) errors.Add(ConstantRampMessage("Top IR"));
                if (_constBed.Enabled && !_constBed.IsValid) errors.Add("Enter a valid constant temperature for the bed.");
                if (_constBedRamp.Enabled && !_constBedRamp.IsValid) errors.Add(ConstantRampMessage("bed"));
            }
            for (int i = 0; i < _rows.Count && !_constant.Checked; i++) {
                var r = _rows[i];
                if (!r.Use.Checked) break;
                if (!r.Ramp.IsValid) errors.Add($"Step {i + 1}: IR ramp rate must be between 0.01 and 10 °C/s.");
                if (!r.BedRamp.IsValid && r.BedRamp.Enabled) errors.Add($"Step {i + 1}: bed ramp rate must be between 0.01 and 10 °C/s.");
                if (!r.Target.IsValid) errors.Add($"Step {i + 1}: enter a valid target temperature.");
                if (!r.Hold.IsValid) errors.Add($"Step {i + 1}: hold time must be 0–9999 s.");
                if (!r.BedTarget.IsValid && r.BedTarget.Enabled) errors.Add($"Step {i + 1}: enter a valid bed heat target.");
            }
            if (!_holdback.IsValid) errors.Add(ReflowProfile.HoldbackRangeMessage);
            if (_bedHoldback.Enabled && !_bedHoldback.IsValid) errors.Add($"Bed holdback must be between {ReflowProfile.HoldbackMin:0} and {ReflowProfile.HoldbackMax:0} °C.");
            return errors;
        }

        private void EditNotes() {
            string name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled profile" : _name.Text.Trim();
            string notes = NotesDialog.Edit(FindForm(), name, _notes);
            if (notes == null || notes == _notes) return;
            _notes = notes;
            MarkDirty();
            UpdateNotesButton();
        }

        private void UpdateNotesButton() => _notesButton.Text = string.IsNullOrWhiteSpace(_notes) ? "Notes" : "Notes •";

        /// <summary>Loads a CSV recording over the preview, or removes the one shown.</summary>
        private void ToggleOverlay() {
            if (_overlayIr.Points.Count > 0 || _overlayBed.Points.Count > 0) {
                _overlayIr.Points.Clear();
                _overlayBed.Points.Clear();
                _overlay.Text = "Overlay run…";
                UpdatePreview();
                return;
            }
            using var d = new OpenFileDialog { Filter = "ReflowLink recordings (*.csv)|*.csv|All files (*.*)|*.*", InitialDirectory = State.Settings.EffectiveLogFolder };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                var run = RecordedRun.Load(d.FileName);
                foreach (var p in run.Ir) _overlayIr.Points.Add(new PointD(p.Time, p.Temp));
                foreach (var p in run.Bed) _overlayBed.Points.Add(new PointD(p.Time, p.Temp));
                _overlay.Text = "Remove overlay";
                State.SetStatus("Overlaid " + Path.GetFileName(d.FileName) + ": time 0 is where the program started", StatusKind.Success);
                UpdatePreview();
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Cannot overlay this file", ex.Message);
            }
        }

        private async Task ShowPatterns() {
            var (choice, slot) = PatternsDialog.Show(FindForm(), State, () => BuildProfile(null), _slot.SelectedIndex);
            if (choice == PatternsDialog.Choice.None) return;
            if (choice == PatternsDialog.Choice.LoadIntoEditor) {
                var p = State.SlotProfiles[slot];
                if (p == null || !ConfirmDiscard()) return;
                _slot.SelectedIndex = slot;
                var copy = p.Clone();
                if (string.IsNullOrWhiteSpace(copy.Name)) copy.Name = $"Pattern {slot} profile";
                LoadProfile(copy, "");
                _dirty = true;
                UpdateHeader();
                State.SetStatus($"Pattern {slot} loaded into the editor", StatusKind.Success);
                return;
            }
            // Write a file: open it in the editor, then the usual checks and confirmation.
            if (!ConfirmDiscard()) return;
            using (var d = new OpenFileDialog { Filter = ReflowProfile.FileFilter, InitialDirectory = EnsureProfilesFolder() }) {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!OpenFile(d.FileName)) return;
            }
            _slot.SelectedIndex = slot;
            await WriteToController();
        }

        private ProfileStep DefaultStepAfter(ReflowProfile p, int index) {
            var last = p.Steps.Count > 0 ? p.Steps[p.Steps.Count - 1] : null;
            int ahead = index - p.Steps.Count + 1;
            double target = last != null ? Math.Min(last.Target + 10 * ahead, State.Settings.MaxTemperature) : 150;
            double bed = last != null ? Math.Min(last.BedTarget + 10 * ahead, State.Settings.Bed.MaxTemperature) : 150;
            return new ProfileStep(1.0, target, 20) { BedTarget = bed };
        }

        /// <summary>
        /// The profile in the editor. In constant temperature mode its steps are still built (they're saved with the file),
        /// but only the constant temperature is checked: use <see cref="ReflowProfile.ForController"/> for what's written.
        /// </summary>
        private ReflowProfile BuildProfile(List<string> errors) {
            bool constant = _constant.Checked;
            var p = new ReflowProfile {
                Name = string.IsNullOrWhiteSpace(_name.Text) ? "Untitled profile" : _name.Text.Trim(),
                Notes = _notes,
                ConstantTemperature = constant,
                ConstantTarget = _constTarget.Value ?? ReflowProfile.DefaultConstantTarget,
                ConstantBedTarget = _constBed.Value ?? ReflowProfile.DefaultConstantTarget,
                ConstantRamp = _constRamp.Value ?? ReflowProfile.DefaultConstantRamp,
                ConstantBedRamp = _constBedRamp.Value ?? ReflowProfile.DefaultConstantRamp,
            };
            if (constant) {
                if (!_constTarget.IsValid) errors?.Add("Enter a valid constant temperature for the Top IR.");
                if (!_constRamp.IsValid) errors?.Add(ConstantRampMessage("Top IR"));
                if (State.BedEnabled && !_constBed.IsValid) errors?.Add("Enter a valid constant temperature for the bed.");
                if (State.BedEnabled && !_constBedRamp.IsValid) errors?.Add(ConstantRampMessage("bed"));
            }
            var stepErrors = constant ? null : errors; // the steps aren't used in constant temperature mode
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                if (!r.Use.Checked) break;
                if (!r.Ramp.IsValid) stepErrors?.Add($"Step {i + 1}: IR ramp rate must be between 0.01 and 10 °C/s.");
                if (!r.Target.IsValid) stepErrors?.Add($"Step {i + 1}: enter a valid target temperature.");
                if (!r.Hold.IsValid) stepErrors?.Add($"Step {i + 1}: hold time must be 0–9999 s.");
                bool bedValid = r.BedTarget.IsValid, bedRampValid = r.BedRamp.IsValid;
                if (State.BedEnabled && !bedValid) stepErrors?.Add($"Step {i + 1}: enter a valid bed heat target.");
                if (State.BedEnabled && !bedRampValid) stepErrors?.Add($"Step {i + 1}: bed ramp rate must be between 0.01 and 10 °C/s.");
                p.Steps.Add(new ProfileStep(r.Ramp.Value ?? 1, r.Target.Value ?? 0, (int)Math.Round(r.Hold.Value ?? 0)) {
                    BedTarget = bedValid ? r.BedTarget.Value.Value : r.Target.Value ?? 0,
                    BedRampRate = bedRampValid ? r.BedRamp.Value.Value : r.Ramp.Value ?? 1,
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
                // What the controller gets: the steps, or the constant temperature pattern.
                var written = p.ForController();
                foreach (var e in written.Validate(State.Settings.MaxTemperature, State.BedEnabled ? State.Settings.Bed.MaxTemperature : null))
                    if (!errors.Contains(e) && !e.Contains("ramp rate must be greater")) errors.Add(Constant(e, constant));
                // When connected, also check against the controllers' own setpoint limits (HS / LS).
                if (State.Device.IsConnected) errors.AddRange(Pc410Device.CheckTargets(written, State.Device.ControllerLimits).Select(e => Constant(e, constant)));
                if (State.DualBed && State.Bed.IsConnected)
                    errors.AddRange(Pc410Device.CheckTargets(written.ForBed(), State.Bed.ControllerLimits)
                        .Select(e => Constant("Bed: " + e.Replace("target", "bed target"), constant)));
            }
            return p;
        }

        private static string ConstantRampMessage(string who) => $"The constant temperature ramp of the {who} must be between 0.01 and 10 °C/s.";

        /// <summary>
        /// A message about a step of the constant temperature pattern, worded for the constant temperature (all 8 steps give
        /// the same message, so it then shows once).
        /// </summary>
        private static string Constant(string message, bool constant) {
            if (!constant) return message;
            message = System.Text.RegularExpressions.Regex.Replace(message, @"Step \d+: bed target", "Constant bed temperature");
            return System.Text.RegularExpressions.Regex.Replace(message, @"Step \d+: target", "Constant temperature");
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
            // In constant temperature mode the steps aren't used: all of them are greyed out (and kept).
            bool constant = _constant.Checked;
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                bool on = r.Use.Checked && !constant;
                r.Ramp.Enabled = r.Target.Enabled = r.Hold.Enabled = on;
                // With a single PC410, ReflowLink runs the bed's program itself, so it can be held (Wait for IR) too.
                r.BedRamp.Enabled = r.BedTarget.Enabled = r.WaitForBed.Enabled = r.WaitForIr.Enabled = on && State.BedEnabled;
                r.Number.ForeColor = on ? Theme.Text : Theme.TextFaint;
                r.Use.Enabled = i > 0 && !constant;
            }
            _bedHoldback.Enabled = State.BedEnabled;
            _constTarget.Enabled = _constRamp.Enabled = constant;
            _constBed.Enabled = _constBedRamp.Enabled = constant && State.BedEnabled;
            _constBedLabel.ForeColor = _constBedAt.ForeColor = State.BedEnabled ? Theme.TextDim : Theme.TextFaint;
            string bedRampTip = State.SingleBed ? "How fast the bed heats up to the constant temperature: ReflowLink ramps the bed setpoint and switches OUT2 on the IN2 reading"
                : State.DualBed ? "How fast the bed heats up to the constant temperature (the bed controller's own pattern)" : "Bed heat control is off";
            _tip.SetToolTip(_constBedRamp, bedRampTip);
            _tip.SetToolTip(_constBedAt, bedRampTip);
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
            var edited = BuildProfile(errors);
            // The preview shows what the controller would run: the steps, or the constant temperature pattern.
            bool constant = edited.ConstantTemperature;
            var p = edited.ForController();
            double start = _startTemp.Value ?? 25;
            bool bed = State.BedEnabled;
            // With bed heat, both heaters' paths: each with its own ramps, lined up by Wait for Bed / Wait for IR (a single
            // PC410's bed too: ReflowLink runs its program).
            var (c, bc) = bed ? ProfileMath.BuildPairedCurves(p, start, start) : (ProfileMath.BuildPlannedCurve(p, start), null);
            _curve.Points.Clear();
            foreach (var pt in c.Points) _curve.Points.Add(new PointD(pt.Time, pt.Temp));
            _bedCurve.Points.Clear();
            if (bed) foreach (var pt in bc.Points) _bedCurve.Points.Add(new PointD(pt.Time, pt.Temp));
            _bedCurve.Visible = bed;
            _overlayBed.Visible = bed;
            _chart.ShowLegend = bed || _overlayIr.Points.Count > 0;
            _overlayIr.ShowInLegend = _overlayIr.Points.Count > 0;
            _overlayBed.ShowInLegend = bed && _overlayBed.Points.Count > 0;
            _chart.Markers.Clear();
            if (constant) {
                _chart.Markers.Add(new ChartMarker { X = 0, Label = "Constant until Stop" });
            } else {
                for (int i = 0; i < c.StepStarts.Count; i++)
                    _chart.Markers.Add(new ChartMarker { X = c.StepStarts[i], Label = "S" + (i + 1) });
            }
            _chart.XMin = 0;
            double total = bed ? Math.Max(c.TotalSeconds, bc.TotalSeconds) : c.TotalSeconds;
            double overlayEnd = _overlayIr.Points.Count > 0 ? _overlayIr.Points[_overlayIr.Points.Count - 1].X : 0;
            // A constant temperature lasts until Stop: show the way up and 15 minutes of it, not the full 22 hours.
            double reached = !constant ? 0 : Math.Max(c.Points.Count > 1 ? c.Points[1].Time : 0, bed && bc.Points.Count > 1 ? bc.Points[1].Time : 0);
            double shownEnd = constant ? reached + 900 : total;
            _chart.XMax = Math.Max(60, Math.Max(shownEnd, overlayEnd) * 1.04);
            _chart.MinXSpan = 60;
            _chart.LimitLine = State.Settings.MaxTemperature;
            _chart.LimitLabel = $"Limit {State.Settings.MaxTemperature:0} °C";
            _chart.Invalidate();

            // Each heater reaches a step's target in its own time; Wait for Bed / Wait for IR make the faster one wait, so the
            // step then takes as long as the slower ramp.
            double rampTime = 0;
            var ramps = ProfileMath.RampTimes(p, start, start);
            var drift = new List<string>();
            for (int i = 0; i < _rows.Count; i++) {
                var r = _rows[i];
                if (constant || i >= p.Steps.Count) {
                    r.Time.Text = "";
                    continue;
                }
                var s = p.Steps[i];
                double ramp = bed ? Math.Max(ramps[i].Ir, ramps[i].Bed) : ramps[i].Ir;
                rampTime += ramp;
                r.Time.Text = ProfileMath.FormatDuration(ramp + s.HoldSeconds);
                if (!bed) continue;
                double late = ramps[i].Bed - ramps[i].Ir;
                if (late > 5 && !s.WaitForBed)
                    drift.Add($"Step {i + 1}: the bed reaches its target {ProfileMath.FormatDuration(late)} after the top IR. Turn on Wait for Bed to keep them in step (warning only).");
                else if (-late > 5 && !s.WaitForIr)
                    drift.Add($"Step {i + 1}: the top IR reaches its target {ProfileMath.FormatDuration(-late)} after the bed. Turn on Wait for IR to keep them in step (warning only).");
            }
            if (constant) rampTime = reached;
            _statTotal.Value = constant ? "Until Stop" : ProfileMath.FormatDuration(total);
            _statPeak.Text = constant ? "Temperature" : "Peak";
            _statPeak.Value = bed ? $"{c.PeakTemp:0}/{bc.PeakTemp:0}" : $"{c.PeakTemp:0.#} °C";
            _statPeak.Invalidate();
            _statRamp.Value = ProfileMath.FormatDuration(rampTime);
            _statSteps.Value = constant ? "Constant" : $"{p.Steps.Count} of 8";
            double above183 = ProfileMath.TimeAbove(c, 183), above217 = ProfileMath.TimeAbove(c, 217);
            _statAbove.Value = constant ? (above183 > 0 ? "Until Stop" : "—")
                : above183 <= 0 ? "—" : $"{ProfileMath.FormatDuration(above183)} / {(above217 > 0 ? ProfileMath.FormatDuration(above217) : "0")}";
            double steepest = Math.Max(ProfileMath.SteepestRamp(p), bed ? ProfileMath.SteepestRamp(p.ForBed()) : 0);
            _statSteepest.Value = $"{steepest:0.##} °C/s";
            _statSteepest.Warn = steepest > ProfileMath.SteepRamp;
            if (steepest > ProfileMath.SteepRamp)
                errors.Add($"A ramp of {steepest:0.##} °C/s is steeper than {ProfileMath.SteepRamp:0} °C/s: components and boards can crack (warning only).");
            errors.AddRange(drift);
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
            OpenFile(d.FileName);
        }

        private bool OpenFile(string path) {
            try {
                var p = ReflowProfile.Load(path);
                LoadProfile(p, path);
                State.Settings.LastProfilePath = path;
                if (p.LoadNote != null) {
                    DarkDialog.Show(FindForm(), "Profile adjusted", p.LoadNote, DialogTone.Warning, ["OK"]);
                    _dirty = true;
                    UpdateHeader();
                }
                State.SetStatus("Opened " + Path.GetFileName(path), StatusKind.Success);
                return true;
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Cannot open profile", ex.InnerException?.Message ?? ex.Message);
                return false;
            }
        }

        private bool SaveProfile(bool saveAs) {
            var invalid = FieldErrors();
            if (invalid.Count > 0) {
                DarkDialog.Error(FindForm(), "Fix the profile first", "Some fields don't hold a valid number, so they can't be saved:\n\n" + string.Join("\n", invalid.Distinct().Take(8)));
                return false;
            }
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
            // The controllers don't store Wait for Bed / Wait for IR (nor, with a single PC410, the bed targets and ramps): use what
            // ReflowLink last wrote to this pattern.
            var sync = State.Settings.SyncFor(slot);
            bool bedRestored = sync?.ApplyTo(p, single) == true;
            if (single && !bedRestored) sync?.ApplyTo(p);
            var known = State.SlotProfiles[slot];
            if (known != null && SameSteps(known, p)) { p.Name = known.Name; p.Notes = known.Notes; }
            // A constant temperature pattern (8 equal steps with the longest holds) opens in constant temperature mode.
            p.MarkConstantIfItIs();
            State.SetSlotProfile(slot, p);
            State.Device.NotePattern(slot);
            if (both) State.Bed.NotePattern(slot);
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.ConstantTemperature ? $"Constant {p.ConstantTarget:0.#} °C" : $"Pattern {slot} profile";
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
                State.SetStatus($"Read pattern {slot}: " + (p.ConstantTemperature ? $"constant temperature {p.ConstantTarget:0.#} °C" : $"{p.Steps.Count} step(s)")
                    + (single ? ", bed targets from ReflowLink" : ""), StatusKind.Success);
        }

        private static bool SameSteps(ReflowProfile a, ReflowProfile b) {
            if (a.Steps.Count != b.Steps.Count) return false;
            for (int i = 0; i < a.Steps.Count; i++)
                if (Math.Abs(a.Steps[i].RampRate - b.Steps[i].RampRate) > 0.011 || Math.Abs(a.Steps[i].BedRampRate - b.Steps[i].BedRampRate) > 0.011
                    || Math.Abs(a.Steps[i].Target - b.Steps[i].Target) > 0.11 || a.Steps[i].HoldSeconds != b.Steps[i].HoldSeconds)
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
            // What the controller gets: the steps, or in constant temperature mode the 8-step pattern that holds it.
            var edited = p;
            p = p.ForController();
            // Waits that don't apply to this setup aren't saved with the pattern (their toggles are greyed out, but a
            // profile file can still carry them): Wait for Bed needs a bed heater, Wait for IR a second PC410.
            foreach (var st in p.Steps) {
                if (!State.BedEnabled) st.WaitForBed = false;
                if (!both) st.WaitForIr = false;
            }
            var curve = ProfileMath.BuildPlannedCurve(p, State.Settings.PreviewStartTemperature);
            string where = both ? $"pattern {slot} of both the Top IR and the Bed Heat controller"
                : single ? $"controller pattern {slot} (ReflowLink keeps the bed targets for it)"
                : $"controller pattern {slot}";
            string what = edited.ConstantTemperature
                ? $"constant temperature {edited.ConstantTarget:0.#} °C at {edited.ConstantRamp:0.####} °C/s"
                    + (State.BedEnabled ? $", bed {edited.ConstantBedTarget:0.#} °C at {edited.ConstantBedRamp:0.####} °C/s" : "")
                    + ", held until Stop (at most about 22 hours)"
                : $"{p.Steps.Count} step(s), peak {curve.PeakTemp:0.#} °C, about {ProfileMath.FormatDuration(curve.TotalSeconds)}";
            if (!DarkDialog.Confirm(FindForm(), $"Write to pattern {slot}?",
                $"\"{p.Name}\" ({what}) will replace whatever is stored in {where}.",
                "Write", "Cancel"))
                return;

            var mode = State.Settings.UnusedSteps;
            bool verify = State.Settings.VerifyAfterWrite;
            var bedMode = State.Settings.Bed.UnusedSteps;
            bool bedVerify = State.Settings.Bed.VerifyAfterWrite;
            // Saved before writing: if ReflowLink closes, crashes or loses power halfway, the pattern is blocked at the next start.
            State.Settings.WritingPattern = slot;
            State.Settings.Save();
            bool ok;
            UploadResult res;
            try {
                (ok, res) = await State.RunPairAsync($"Writing pattern {slot}",
                    (ir, bed, progress, ct) => PatternPair.Write(ir, bed, p, slot, mode, verify, progress, ct, bedMode, bedVerify));
            } finally {
                State.Settings.WritingPattern = -1;
                State.Settings.Save();
            }
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
            // Holdback is one setting for every pattern, so it also changes a program that's running now.
            bool running = channel.Device.Last?.State is ProgramState.Running or ProgramState.Hold;
            if (running && !DarkDialog.Confirm(FindForm(), $"Change the {channel.Name} holdback during a run?",
                    $"The {channel.Name} controller is running a program. The new holdback ({value:0.#} °C) applies to it straight away.",
                    "Write", "Cancel"))
                return;
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
