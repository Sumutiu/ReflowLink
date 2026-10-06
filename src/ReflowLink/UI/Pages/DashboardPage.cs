// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>Key/value line used in side panels.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class InfoRow : ThemedControl {
        private string _value = "—";
        private Color _valueColor = Theme.Text;

        public InfoRow(string label) {
            Text = label;
            Height = Theme.S(28);
        }

        public string Value { get => _value; set { if (_value == value) return; _value = value; Invalidate(); } }
        public Color ValueColor { get => _valueColor; set { _valueColor = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e) {
            TextRenderer.DrawText(e.Graphics, Text, Theme.Body, new Rectangle(0, 0, Width / 2, Height), Theme.TextDim, Theme.LeftMiddle);
            TextRenderer.DrawText(e.Graphics, _value, Theme.BodyBold, new Rectangle(Width / 3, 0, Width - Width / 3, Height), _valueColor, Theme.RightMiddle);
        }
    }

    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class DashboardPage : PageBase {
        private readonly StatCard _pvCard, _spCard, _opCard, _stateCard;
        private readonly ChartView _chart;
        private readonly ChartSeries _pv, _sp, _op, _plan;
        private readonly ModernDropDown _window, _slot;
        private readonly ModernButton _run, _hold, _stop, _openLogs;
        private readonly ToggleSwitch _record;
        private readonly InfoRow _rowState, _rowStep, _rowElapsed, _rowPlanned, _rowRemaining;
        private readonly ThemedLabel _recordInfo;

        private DateTime _sessionStart = DateTime.Now;
        private double? _runStart;
        private PlannedCurve _runPlan;
        private int? _runPattern;
        private ProgramState _state = ProgramState.Unknown;
        private CsvRecorder _recorder;
        private bool _autoRecording, _settingToggle;
        private DateTime? _autoStopAt;
        private double _lastX;

        private static readonly string[] WindowNames = ["Current run", "Last 2 min", "Last 5 min", "Last 15 min", "Whole session"];
        private static readonly double[] WindowSeconds = [0, 120, 300, 900, 0];

        public DashboardPage(AppState state) : base(state, "Dashboard", "Live readings and program control") {
            // ---------------------------------------------------------------- stat cards
            _pvCard = new StatCard("Temperature", "°C", Theme.SeriesPv) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(8), 0) };
            _spCard = new StatCard("Setpoint", "°C", Theme.SeriesSp) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0) };
            _opCard = new StatCard("Heater output", "%", Theme.SeriesOp) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0) };
            _stateCard = new StatCard("Program", "", Theme.TextFaint) { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0) };

            var stats = Table(4, 1);
            stats.Dock = DockStyle.Fill;
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Controls.Add(_pvCard, 0, 0);
            stats.Controls.Add(_spCard, 1, 0);
            stats.Controls.Add(_opCard, 2, 0);
            stats.Controls.Add(_stateCard, 3, 0);

            // ---------------------------------------------------------------- chart card
            _pv = new ChartSeries { Name = "Temperature (PV)", Color = Theme.SeriesPv, Width = 2.2f };
            _sp = new ChartSeries { Name = "Setpoint (SP)", Color = Theme.SeriesSp, Width = 1.6f };
            _op = new ChartSeries { Name = "Output", Color = Theme.SeriesOp, Width = 1.2f, FillArea = true, Secondary = true, Unit = "%", Format = "0" };
            _plan = new ChartSeries { Name = "Planned", Color = Theme.SeriesPlan, Width = 1.4f, Dashed = true };
            _chart = new ChartView { Dock = DockStyle.Fill, EmptyText = "Connect to the controller to see live data" };
            _chart.Series.Add(_op);
            _chart.Series.Add(_plan);
            _chart.Series.Add(_sp);
            _chart.Series.Add(_pv);

            _window = new ModernDropDown { Width = Theme.S(150), Margin = new Padding(0, 0, Theme.S(8), 0) };
            _window.SetItems(WindowNames, 2);
            _window.SelectedIndexChanged += (s, e) => UpdateWindow();
            var export = new ModernButton("", Glyph.Image) { Margin = new Padding(0, 0, Theme.S(8), 0) };
            export.Click += (s, e) => ExportPng();
            var clear = new ModernButton("", Glyph.Trash) { Margin = Padding.Empty };
            clear.Click += (s, e) => ClearChart();
            var tip = Theme.CreateToolTip();
            tip.SetToolTip(export, "Export chart as PNG");
            tip.SetToolTip(clear, "Clear chart");

            var chartHeader = new CardHeader("Live temperature", "Board temperature, setpoint and heater output");
            chartHeader.Tools.Controls.AddRange([_window, export, clear]);
            var chartCard = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(8), 0) };
            chartCard.Controls.Add(_chart);
            chartCard.Controls.Add(chartHeader);
            _chart.BringToFront();

            // ---------------------------------------------------------------- control card
            var side = new CardPanel { Dock = DockStyle.Fill, Margin = new Padding(Theme.S(8), 0, 0, 0), AutoScroll = true };
            Theme.ApplyDarkScrollbars(side);
            var stack = Table(1, 0);
            stack.Dock = DockStyle.Top;
            stack.AutoSize = true;
            stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stack.AutoScroll = false;

            void Add(Control c, int top = 0) {
                c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                c.Margin = new Padding(0, Theme.S(top), 0, 0);
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stack.Controls.Add(c, 0, stack.RowCount++);
            }

            Add(new CardHeader("Program control"));
            Add(new ThemedLabel("Controller pattern", Theme.Small, Theme.TextDim), 2);
            _slot = new ModernDropDown();
            _slot.SetItems(State.SlotLabels(), State.Settings.LastSlot);
            _slot.SelectedIndexChanged += (s, e) => { State.Settings.LastSlot = _slot.SelectedIndex; ShowPlannedPreview(); };
            Add(_slot, 6);

            _run = new ModernButton("Start program", Glyph.Play, ButtonKind.Success) { AutoWidth = false, Height = Theme.S(42) };
            _run.Click += async (s, e) => await StartProgram();
            Add(_run, 14);

            var row = Table(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(38)));
            row.Height = Theme.S(38);
            _hold = new ModernButton("Hold", Glyph.Pause) { AutoWidth = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(4), 0) };
            _hold.Click += async (s, e) => await HoldOrResume();
            _stop = new ModernButton("Stop", Glyph.Stop, ButtonKind.Danger) { AutoWidth = false, Dock = DockStyle.Fill, Margin = new Padding(Theme.S(4), 0, 0, 0) };
            _stop.Click += async (s, e) => await StopProgram();
            row.Controls.Add(_hold, 0, 0);
            row.Controls.Add(_stop, 1, 0);
            Add(row, 8);

            Add(new Divider(), 12);
            _rowState = new InfoRow("State");
            _rowStep = new InfoRow("Segment");
            _rowElapsed = new InfoRow("Elapsed");
            _rowPlanned = new InfoRow("Planned duration");
            _rowRemaining = new InfoRow("Remaining (est.)");
            Add(_rowState);
            Add(_rowStep);
            Add(_rowElapsed);
            Add(_rowPlanned);
            Add(_rowRemaining);

            Add(new Divider(), 8);
            _record = new ToggleSwitch("Record to CSV");
            _record.CheckedChanged += (s, e) => { if (!_settingToggle) { if (_record.Checked) StartRecording(false); else StopRecording(); } };
            Add(_record, 2);
            _recordInfo = new ThemedLabel(State.Settings.AutoRecordRuns ? "Runs are recorded automatically." : "Recording is off.", Theme.Small, Theme.TextDim) {
                AutoSize = false,
                Height = Theme.S(36),
                AutoEllipsis = true,
            };
            Add(_recordInfo, 4);
            _openLogs = new ModernButton("Open logs folder", Glyph.Folder, ButtonKind.Ghost);
            _openLogs.Click += (s, e) => OpenFolder(State.Settings.EffectiveLogFolder);
            _openLogs.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(_openLogs, 0, stack.RowCount++);
            _openLogs.Margin = new Padding(-Theme.S(8), Theme.S(4), 0, 0);

            side.Controls.Add(stack);

            // ---------------------------------------------------------------- root layout
            var main = Table(2, 1);
            main.Dock = DockStyle.Fill;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(310)));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.Controls.Add(chartCard, 0, 0);
            main.Controls.Add(side, 1, 0);

            var root = Table(1, 2);
            root.Dock = DockStyle.Fill;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(118 + 16)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stats.Margin = new Padding(0, 0, 0, Theme.S(16));
            root.Controls.Add(stats, 0, 0);
            root.Controls.Add(main, 0, 1);
            Controls.Add(root);

            // ---------------------------------------------------------------- events
            State.Device.TelemetryReceived += OnTelemetry;
            State.Device.StateChanged += (s, e) => OnLinkChanged();
            State.Device.ProgramStateChanged += OnProgramStateChanged;
            State.SlotProfilesChanged += () => { _slot.SetItems(State.SlotLabels(), _slot.SelectedIndex); ShowPlannedPreview(); };
            State.BusyChanged += UpdateButtons;
            OnLinkChanged();
            ShowPlannedPreview();
        }

        // ==================================================================== link / telemetry

        private LinkStatus _lastLink = LinkStatus.Disconnected;

        private void OnLinkChanged() {
            var ls = State.Device.State;
            if (ls == LinkStatus.Connected && (_lastLink == LinkStatus.Disconnected || _lastLink == LinkStatus.Connecting)) {
                ClearChart();
                if (State.Device.ActivePattern.HasValue) _slot.SelectedIndex = State.Device.ActivePattern.Value;
            }
            if (ls == LinkStatus.Disconnected) {
                _state = ProgramState.Unknown;
                _pvCard.Value = _spCard.Value = _opCard.Value = "—";
                _pvCard.Detail = _spCard.Detail = _opCard.Detail = "Not connected";
                _opCard.Bar = null;
                _stateCard.Value = "Offline";
                _stateCard.Accent = Theme.TextFaint;
                _stateCard.Detail = "Connect from the sidebar";
                _rowState.Value = "—";
                _chart.EmptyText = "Connect to the controller to see live data";
                if (_recorder != null) StopRecording();
            } else if (ls == LinkStatus.NoResponse) {
                _stateCard.Detail = "No response from controller";
                _stateCard.Accent = Theme.Red;
            } else if (ls == LinkStatus.Connected) {
                _chart.EmptyText = "Waiting for data…";
            }
            _lastLink = ls;
            UpdateButtons();
            _chart.Invalidate();
        }

        private void OnTelemetry(Telemetry t) {
            double x = (t.Time - _sessionStart).TotalSeconds * State.Device.TimeScale;
            _lastX = x;
            if (t.Pv.HasValue) _pv.Points.Add(new PointD(x, t.Pv.Value));
            if (t.Sp.HasValue) _sp.Points.Add(new PointD(x, t.Sp.Value));
            if (t.Op.HasValue) _op.Points.Add(new PointD(x, t.Op.Value));
            const int cap = 250000;
            foreach (var s in new[] { _pv, _sp, _op })
                if (s.Points.Count > cap) s.Points.RemoveRange(0, s.Points.Count - cap);

            if (t.State != ProgramState.Unknown) _state = t.State;

            // Cards
            _pvCard.Value = t.Pv.HasValue ? t.Pv.Value.ToString("0.0") : "—";
            double? rate = PvRate(10);
            _pvCard.Detail = rate.HasValue ? $"Rate {(rate.Value >= 0 ? "+" : "")}{rate.Value:0.00} °C/s" : "";
            _spCard.Value = t.Sp.HasValue ? t.Sp.Value.ToString("0.0") : "—";
            if (t.Pv.HasValue && t.Sp.HasValue && _state != ProgramState.Idle) {
                double d = t.Pv.Value - t.Sp.Value;
                _spCard.Detail = $"Deviation {(d >= 0 ? "+" : "")}{d:0.0} °C";
            } else _spCard.Detail = _state == ProgramState.Idle ? "Program not running" : "";
            _opCard.Value = t.Op.HasValue ? t.Op.Value.ToString("0") : "—";
            _opCard.Bar = t.Op.HasValue ? t.Op.Value / 100.0 : (double?)null;
            _opCard.Detail = t.StatusWord.HasValue ? $"Status word {Pc410Protocol.FormatHexWord(t.StatusWord.Value)}" : (_opCard.Detail ?? "");
            if (string.IsNullOrEmpty(_opCard.Detail) || _opCard.Detail == "Not connected") _opCard.Detail = "Heater power";

            UpdateProgramInfo(t);

            if (_recorder != null) {
                try { _recorder.Write(t); } catch (Exception ex) { State.SetStatus("Recording stopped: " + ex.Message, StatusKind.Error); StopRecording(); }
                if (_autoStopAt.HasValue && DateTime.Now >= _autoStopAt.Value) StopRecording();
                else if (_recorder != null) _recordInfo.Text = $"Recording · {_recorder.Rows} rows\n{Path.GetFileName(_recorder.FilePath)}";
            }

            UpdateWindow();
            UpdateButtons();
        }

        private double? PvRate(double seconds) {
            var pts = _pv.Points;
            if (pts.Count < 3) return null;
            var last = pts[pts.Count - 1];
            int i = pts.Count - 1;
            while (i > 0 && last.X - pts[i].X < seconds) i--;
            var first = pts[i];
            if (last.X - first.X < 2) return null;
            return (last.Y - first.Y) / (last.X - first.X);
        }

        private void UpdateProgramInfo(Telemetry t) {
            string st;
            Color c;
            switch (_state) {
                case ProgramState.Running: st = "Running"; c = Theme.Green; break;
                case ProgramState.Hold: st = "On hold"; c = Theme.Yellow; break;
                case ProgramState.Idle: st = "Idle"; c = Theme.TextDim; break;
                default: st = "Unknown"; c = Theme.TextFaint; break;
            }
            if (State.Device.State == LinkStatus.NoResponse) { c = Theme.Red; }
            _stateCard.Value = st;
            _stateCard.Accent = c;
            _rowState.Value = st;
            _rowState.ValueColor = c;

            int? pattern = _runPattern ?? State.Device.ActivePattern;
            bool active = _state is ProgramState.Running or ProgramState.Hold;
            int steps = _runPlan != null ? _runPlan.StepStarts.Count : 0;
            string seg = t?.Segment.HasValue == true && t.Segment.Value > 0
                ? (steps > 0 ? $"{t.Segment.Value} of {steps}" : t.Segment.Value.ToString())
                : "—";
            _rowStep.Value = active ? seg : "—";

            if (active && _runStart.HasValue) {
                double el = _lastX - _runStart.Value;
                _rowElapsed.Value = ProfileMath.FormatDuration(el);
                _rowRemaining.Value = _runPlan != null ? ProfileMath.FormatDuration(Math.Max(0, _runPlan.TotalSeconds - el)) : "—";
                _stateCard.Detail = $"Pattern {pattern?.ToString() ?? "?"} · {ProfileMath.FormatDuration(el)} elapsed";
            } else {
                _rowElapsed.Value = "—";
                _rowRemaining.Value = "—";
                _stateCard.Detail = State.Device.State == LinkStatus.NoResponse ? "No response from controller"
                    : pattern.HasValue ? $"Active pattern {pattern}" : "Ready";
            }
        }

        private void OnProgramStateChanged(ProgramState prev, ProgramState now) {
            bool wasActive = prev is ProgramState.Running or ProgramState.Hold;
            if (now == ProgramState.Running && !wasActive) {
                // A run has started (from this app or the front panel).
                _runStart = _lastX;
                _runPattern = State.Device.ActivePattern;
                _plan.Points.Clear();
                _chart.Markers.Clear();
                _runPlan = null;
                var prof = _runPattern.HasValue ? State.SlotProfiles[_runPattern.Value] : null;
                if (prof != null && prev != ProgramState.Unknown) {
                    double start = _pv.Points.Count > 0 ? _pv.Points[_pv.Points.Count - 1].Y : State.Settings.PreviewStartTemperature;
                    _runPlan = ProfileMath.BuildPlannedCurve(prof, start);
                    foreach (var p in _runPlan.Points) _plan.Points.Add(new PointD(_runStart.Value + p.Time, p.Temp));
                    for (int i = 0; i < _runPlan.StepStarts.Count; i++)
                        _chart.Markers.Add(new ChartMarker { X = _runStart.Value + _runPlan.StepStarts[i], Label = "S" + (i + 1) });
                    _rowPlanned.Value = ProfileMath.FormatDuration(_runPlan.TotalSeconds);
                } else {
                    _chart.Markers.Add(new ChartMarker { X = _runStart.Value, Label = prev == ProgramState.Unknown ? "Run in progress" : "Start" });
                    _rowPlanned.Value = "—";
                }
                _window.SelectedIndex = 0;
                State.SetStatus($"Program started (pattern {_runPattern?.ToString() ?? "?"})", StatusKind.Success);
                if (State.Settings.AutoRecordRuns && _recorder == null) StartRecording(true);
                else if (_autoRecording) _autoStopAt = null;
            } else if (now == ProgramState.Idle && wasActive) {
                _chart.Markers.Add(new ChartMarker { X = _lastX, Label = "End", Color = Theme.Green });
                State.SetStatus("Program finished", StatusKind.Success);
                if (_autoRecording && _recorder != null)
                    _autoStopAt = DateTime.Now.AddSeconds(State.Settings.RecordAfterRunSeconds);
                _runPattern = null;
            }
            UpdateProgramInfo(State.Device.Last);
            UpdateButtons();
        }

        private void ShowPlannedPreview() {
            if (_state is ProgramState.Running or ProgramState.Hold) return;
            int slot = _slot.SelectedIndex;
            var p = slot >= 0 ? State.SlotProfiles[slot] : null;
            _rowPlanned.Value = p != null ? ProfileMath.FormatDuration(ProfileMath.BuildPlannedCurve(p, State.Settings.PreviewStartTemperature).TotalSeconds) : "—";
        }

        // ==================================================================== chart window

        private void UpdateWindow() {
            int w = _window.SelectedIndex;
            double now = _lastX;
            if (w == 0) {
                if (_runStart.HasValue) {
                    _chart.XMin = Math.Max(0, _runStart.Value - 10);
                    double end = _runPlan != null ? _runStart.Value + _runPlan.TotalSeconds + 30 : now;
                    _chart.XMax = Math.Max(now + 5, end);
                    _chart.MinXSpan = 60;
                } else { _chart.XMin = Math.Max(0, now - 300); _chart.XMax = Math.Max(now, 300); _chart.MinXSpan = 300; }
            } else if (w is >= 1 and <= 3) {
                double span = WindowSeconds[w];
                _chart.XMin = Math.Max(0, now - span);
                _chart.XMax = Math.Max(now, span);
                _chart.MinXSpan = span;
            } else {
                _chart.XMin = 0;
                _chart.XMax = Math.Max(now, 60);
                _chart.MinXSpan = 60;
            }
            _chart.LimitLine = State.Settings.MaxTemperature;
            _chart.LimitLabel = $"Limit {State.Settings.MaxTemperature:0} °C";
            _chart.Invalidate();
        }

        private void ClearChart() {
            _sessionStart = DateTime.Now;
            _lastX = 0;
            _pv.Points.Clear();
            _sp.Points.Clear();
            _op.Points.Clear();
            _plan.Points.Clear();
            _chart.Markers.Clear();
            _runStart = null;
            _runPlan = null;
            UpdateWindow();
        }

        private void ExportPng() {
            using var d = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"ReflowLink_{DateTime.Now:yyyy-MM-dd_HHmmss}.png" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                using (var bmp = _chart.Snapshot()) bmp.Save(d.FileName, ImageFormat.Png);
                State.SetStatus("Chart exported to " + d.FileName, StatusKind.Success);
            } catch (Exception ex) { DarkDialog.Error(FindForm(), "Cannot export chart", ex.Message); }
        }

        // ==================================================================== program control

        private void UpdateButtons() {
            bool conn = State.Device.IsConnected;
            bool busy = State.Busy;
            bool active = _state is ProgramState.Running or ProgramState.Hold;
            _run.Enabled = conn && !busy && !active;
            _slot.Enabled = !busy && !active;
            _stop.Enabled = conn && !busy;
            _hold.Enabled = conn && !busy && active;
            bool resume = _state == ProgramState.Hold;
            if (_hold.Text != (resume ? "Resume" : "Hold")) {
                _hold.Text = resume ? "Resume" : "Hold";
                _hold.Glyph = resume ? Glyph.Play : Glyph.Pause;
            }
        }

        private async System.Threading.Tasks.Task StartProgram() {
            int slot = _slot.SelectedIndex;
            if (slot < 0) return;
            bool needRead = State.SlotProfiles[slot] == null;
            var (ok, prof) = await State.RunAsync($"Starting pattern {slot}", (d, progress, ct) => {
                ReflowProfile p = null;
                if (needRead) p = d.DownloadProfile(slot, progress, ct);
                d.Start(slot);
                return p;
            });
            if (!ok) return;
            if (prof != null) State.SetSlotProfile(slot, prof);
            State.Device.NotePattern(slot);
        }

        private async System.Threading.Tasks.Task HoldOrResume() {
            if (_state == ProgramState.Hold) await State.RunAsync("Resuming program", d => d.Resume());
            else await State.RunAsync("Holding program", d => d.Hold());
        }

        private async System.Threading.Tasks.Task StopProgram() {
            await State.RunAsync("Stopping program", d => d.Stop());
        }

        // ==================================================================== recording

        private void StartRecording(bool auto) {
            if (_recorder != null) return;
            try {
                int? pattern = _runPattern ?? State.Device.ActivePattern;
                var prof = pattern.HasValue ? State.SlotProfiles[pattern.Value] : null;
                string label = prof != null && !string.IsNullOrWhiteSpace(prof.Name) ? prof.Name
                    : pattern.HasValue ? "pattern" + pattern.Value : "session";
                _recorder = new CsvRecorder(State.Settings.EffectiveLogFolder, label);
                _autoRecording = auto;
                _autoStopAt = null;
                _recordInfo.Text = "Recording · 0 rows\n" + Path.GetFileName(_recorder.FilePath);
                SetToggle(true);
                State.SetStatus("Recording to " + _recorder.FilePath);
            } catch (Exception ex) {
                _recorder = null;
                SetToggle(false);
                DarkDialog.Error(FindForm(), "Cannot start recording", ex.Message);
            }
        }

        private void StopRecording() {
            if (_recorder == null) { SetToggle(false); return; }
            var r = _recorder;
            _recorder = null;
            r.Dispose();
            _autoRecording = false;
            _autoStopAt = null;
            _recordInfo.Text = $"Saved {r.Rows} rows\n{Path.GetFileName(r.FilePath)}";
            SetToggle(false);
            State.SetStatus("Recording saved: " + r.FilePath, StatusKind.Success);
        }

        private void SetToggle(bool on) {
            _settingToggle = true;
            _record.Checked = on;
            _settingToggle = false;
        }

        public static void OpenFolder(string folder) {
            try {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            } catch { }
        }

        public bool IsProgramActive => _state is ProgramState.Running or ProgramState.Hold;

        public void Shutdown() {
            if (_recorder != null) StopRecording();
        }
    }
}
