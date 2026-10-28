// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI {
    /// <summary>
    /// Overview of the ten controller patterns: what ReflowLink knows about each, reading them all at once, loading one
    /// into the editor, comparing one with the editor, or writing a profile file to one.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class PatternsDialog : Form {
        private readonly AppState _state;
        private readonly Func<ReflowProfile> _editorProfile;
        private readonly PatternList _list;
        // Patterns read in this dialog that turned out to be empty (as opposed to not read yet).
        private readonly HashSet<int> _empty = [];
        private readonly ModernButton _readAll, _load, _compare, _writeFile;

        /// <summary>What the user chose to do once the dialog closes.</summary>
        public enum Choice { None, LoadIntoEditor, WriteFile }

        public Choice Result { get; private set; }
        public int Slot => _list.Selected;

        private PatternsDialog(AppState state, Func<ReflowProfile> editorProfile, int slot) {
            _state = state;
            _editorProfile = editorProfile;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Text = "Controller patterns";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(760), Theme.S(36 * 11 + 40 + 64 + 40));

            _list = new PatternList(state, _empty) { Dock = DockStyle.Fill, Selected = Math.Max(0, slot) };
            _list.SelectedChanged += UpdateButtons;
            _list.DoubleClick += (s, e) => Choose(Choice.LoadIntoEditor);
            var hint = new ThemedLabel(state.DualBed
                    ? "Patterns are read from both controllers. Reading selects each pattern in turn; the selected pattern is put back afterwards."
                    : "Reading selects each pattern in turn on the controller; the selected pattern is put back afterwards.",
                Theme.Small, Theme.TextDim) { AutoSize = false, Dock = DockStyle.Top, Height = Theme.S(36) };
            var body = new Panel { Dock = DockStyle.Fill, Padding = Theme.Pad(20, 14, 20, 8), BackColor = Theme.Surface };
            body.Controls.Add(_list);
            body.Controls.Add(hint);

            // Read all on the left, the rest on the right, in columns that can't overlap; the window grows to fit them.
            var footer = new TableLayoutPanel {
                Dock = DockStyle.Bottom,
                Height = Theme.S(64),
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(Theme.S(16), Theme.S(15), Theme.S(16), 0),
                BackColor = Theme.Sidebar,
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _readAll = new ModernButton("Read all", Glyph.Download) { Margin = Padding.Empty };
            _readAll.Click += async (s, e) => await ReadAll();

            var flow = new FlowLayoutPanel {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Theme.Sidebar,
            };
            var close = new ModernButton("Close") { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            close.Click += (s, e) => Close();
            _load = new ModernButton("Load into editor", Glyph.None, ButtonKind.Primary) { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            _load.Click += (s, e) => Choose(Choice.LoadIntoEditor);
            _compare = new ModernButton("Compare with editor") { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            _compare.Click += (s, e) => Compare();
            _writeFile = new ModernButton("Write a file…", Glyph.Upload) { Margin = new Padding(Theme.S(8), 0, 0, 0) };
            _writeFile.Click += (s, e) => Choose(Choice.WriteFile);
            flow.Controls.AddRange([close, _load, _compare, _writeFile]);
            footer.Controls.Add(_readAll, 0, 0);
            footer.Controls.Add(flow, 2, 0);
            // Button widths depend on the font and display scaling: make room for all of them with a gap in between.
            int needed = footer.Padding.Horizontal + _readAll.Width + Theme.S(24) + flow.GetPreferredSize(Size.Empty).Width;
            if (needed > ClientSize.Width) ClientSize = new Size(needed, ClientSize.Height);
            Controls.Add(body);
            Controls.Add(footer);
            CancelButton = close;

            var tip = Theme.CreateToolTip();
            tip.SetToolTip(_readAll, "Read patterns 0–9 from the controller" + (state.DualBed ? "s" : ""));
            tip.SetToolTip(_writeFile, "Open a profile file and write it to the selected pattern");
            state.BusyChanged += UpdateButtons;
            UpdateButtons();
        }

        protected override void OnFormClosed(FormClosedEventArgs e) {
            _state.BusyChanged -= UpdateButtons;
            base.OnFormClosed(e);
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        /// <summary>Shows the dialog. The caller acts on <see cref="Result"/> and <see cref="Slot"/> afterwards.</summary>
        public static (Choice Choice, int Slot) Show(IWin32Window owner, AppState state, Func<ReflowProfile> editorProfile, int slot) {
            using var d = new PatternsDialog(state, editorProfile, slot);
            d.ShowDialog(owner);
            return (d.Result, d.Slot);
        }

        private void UpdateButtons() {
            if (IsDisposed) return;
            bool busy = _state.Busy;
            bool connected = _state.Device.IsConnected && (!_state.DualBed || _state.Bed.IsConnected);
            var known = _state.SlotProfiles[_list.Selected];
            _readAll.Enabled = connected && !busy;
            _load.Enabled = known != null && !busy;
            _compare.Enabled = known != null && !busy;
            _writeFile.Enabled = connected && !busy;
            _list.Invalidate();
        }

        private void Choose(Choice c) {
            if (c == Choice.LoadIntoEditor && _state.SlotProfiles[_list.Selected] == null) return;
            Result = c;
            Close();
        }

        private async Task ReadAll() {
            bool both = _state.DualBed;
            var (ok, read) = await _state.RunPairAsync(both ? "Reading all patterns from both controllers" : "Reading all patterns", (ir, bed, progress, ct) => {
                int original = ir.ReadPattern();
                int bedOriginal = bed?.ReadPattern() ?? -1;
                var results = new PairRead[10];
                try {
                    for (int slot = 0; slot <= 9; slot++) {
                        ct.ThrowIfCancellationRequested();
                        int s = slot;
                        var inner = progress == null ? null : new Progress<OperationProgress>(p => progress.Report(new OperationProgress(s, 10, $"Pattern {s}: {p.Text}")));
                        results[slot] = PatternPair.Read(ir, bed, slot, inner, ct);
                    }
                } finally {
                    // Put back the pattern that was selected before.
                    try { ir.SelectPattern(original); } catch (Pc410Exception) { }
                    try { bed?.SelectPattern(bedOriginal); } catch (Pc410Exception) { }
                }
                return results;
            });
            if (!ok) return;
            int mismatched = 0;
            for (int slot = 0; slot <= 9; slot++) {
                var p = read[slot].Profile;
                if (read[slot].Warnings.Count > 0) mismatched++;
                var sync = _state.Settings.SyncFor(slot);
                if (sync?.ApplyTo(p, _state.SingleBed) != true) sync?.ApplyTo(p);
                var known = _state.SlotProfiles[slot];
                if (known != null && known.Steps.Count == p.Steps.Count) {
                    p.Name = known.Name;
                    p.Notes = known.Notes;
                }
                if (p.Steps.Count == 0) _empty.Add(slot);
                else _empty.Remove(slot);
                _state.SetSlotProfile(slot, p.Steps.Count > 0 ? p : null);
            }
            _state.SetStatus(mismatched == 0 ? "Read all 10 patterns" : $"Read all 10 patterns; {mismatched} differ between the two controllers",
                mismatched == 0 ? StatusKind.Success : StatusKind.Warning);
            UpdateButtons();
        }

        private void Compare() {
            var stored = _state.SlotProfiles[_list.Selected];
            var editor = _editorProfile();
            if (stored == null || editor == null) return;
            var diff = ProfileMath.Differences(editor, stored, _state.BedEnabled);
            DarkDialog.Show(this, $"Editor vs pattern {_list.Selected}",
                diff.Count == 0 ? "The editor holds the same steps as this pattern." : string.Join("\n", diff.Take(16)) + (diff.Count > 16 ? $"\n… and {diff.Count - 16} more." : ""),
                diff.Count == 0 ? DialogTone.Info : DialogTone.Question, ["OK"]);
        }

        /// <summary>The ten patterns, one row each; click to select.</summary>
        [System.ComponentModel.DesignerCategory("Code")]
        private sealed class PatternList : ThemedControl {
            private readonly AppState _state;
            private readonly HashSet<int> _empty;
            private int _selected;

            public PatternList(AppState state, HashSet<int> empty) {
                _state = state;
                _empty = empty;
                Cursor = Cursors.Hand;
            }

            public event Action SelectedChanged;

            public int Selected {
                get => _selected;
                set {
                    int v = Math.Max(0, Math.Min(9, value));
                    if (v == _selected) return;
                    _selected = v;
                    Invalidate();
                    SelectedChanged?.Invoke();
                }
            }

            private int RowHeight => Theme.S(36);

            protected override void OnMouseDown(MouseEventArgs e) {
                base.OnMouseDown(e);
                int row = e.Y / RowHeight - 1;
                if (row is >= 0 and <= 9) Selected = row;
            }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                Theme.HighQuality(g);
                int h = RowHeight;
                // The profile column takes whatever width the fixed columns on the right leave.
                int peak = Math.Max(Theme.S(330), Width - Theme.S(300));
                int[] x = [Theme.S(10), Theme.S(90), peak, peak + Theme.S(90), peak + Theme.S(180)];
                string[] heads = ["PATTERN", "PROFILE", "PEAK", "LENGTH", "STATUS"];
                for (int c = 0; c < heads.Length; c++)
                    TextRenderer.DrawText(g, heads[c], Theme.SmallBold, new Rectangle(x[c], 0, Theme.S(200), h), Theme.TextFaint, Theme.LeftMiddle);
                double start = _state.Settings.PreviewStartTemperature;
                for (int slot = 0; slot <= 9; slot++) {
                    var r = new Rectangle(0, (slot + 1) * h, Width - 1, h - 2);
                    if (slot == _selected) {
                        using var p = Theme.Rounded(r, Theme.Sf(6));
                        using var b = new SolidBrush(Theme.WithAlpha(Theme.Accent, 40));
                        g.FillPath(b, p);
                    }
                    var known = _state.SlotProfiles[slot];
                    bool blocked = _state.IsSlotBlocked(slot);
                    string name = known == null ? (_empty.Contains(slot) ? "Empty" : "Not read yet") : string.IsNullOrWhiteSpace(known.Name) ? $"{known.Steps.Count} step(s)" : $"{known.Name} · {known.Steps.Count} step(s)";
                    var curve = known != null ? ProfileMath.BuildPlannedCurve(known, start) : null;
                    string status = blocked ? "Rejected: rewrite" : _state.Device.ActivePattern == slot ? "Selected" : "";
                    Color text = known == null ? Theme.TextFaint : Theme.Text;
                    TextRenderer.DrawText(g, slot.ToString(CultureInfo.InvariantCulture), Theme.BodyBold, new Rectangle(x[0], r.Top, Theme.S(70), h), Theme.TextDim, Theme.LeftMiddle);
                    TextRenderer.DrawText(g, name, Theme.Body, new Rectangle(x[1], r.Top, x[2] - x[1] - Theme.S(8), h), text, Theme.LeftMiddle);
                    TextRenderer.DrawText(g, curve != null ? $"{curve.PeakTemp:0} °C" : "—", Theme.Body, new Rectangle(x[2], r.Top, Theme.S(76), h), text, Theme.LeftMiddle);
                    TextRenderer.DrawText(g, curve != null ? ProfileMath.FormatDuration(curve.TotalSeconds) : "—", Theme.Body, new Rectangle(x[3], r.Top, Theme.S(76), h), text, Theme.LeftMiddle);
                    TextRenderer.DrawText(g, status, Theme.Small, new Rectangle(x[4], r.Top, Width - x[4], h), blocked ? Theme.Red : Theme.Green, Theme.LeftMiddle);
                }
            }
        }
    }
}
