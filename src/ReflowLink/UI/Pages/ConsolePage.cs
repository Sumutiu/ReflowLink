// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ReflowLink.Protocol;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class ConsolePage : PageBase {
        private const int MaxLines = 4000;

        private readonly RichTextBox _log;
        private readonly ToggleSwitch _showPoll, _showHex, _pause;
        private readonly ModernTextBox _mnemonic, _value;
        private readonly ModernButton _readBtn, _writeBtn;
        private readonly StringBuilder _plain = new();
        private int _lines;

        public ConsolePage(AppState state) : base(state, "Console", "Raw serial traffic and manual parameter access") {
            // ---------------------------------------------------------------- manual access card
            _mnemonic = new ModernTextBox {
                Width = Theme.S(80),
                Margin = new Padding(0, 0, Theme.S(8), 0),
                Placeholder = "PV"
            };
            _mnemonic.InnerTextBox.MaxLength = 2;
            _mnemonic.InnerTextBox.CharacterCasing = CharacterCasing.Normal;
            _value = new ModernTextBox {
                Width = Theme.S(140),
                Margin = new Padding(0, 0, Theme.S(8), 0),
                Placeholder = "value (for write)"
            };
            _readBtn = new ModernButton("Read", Glyph.Download) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            _readBtn.Click += async (s, e) => await ManualRead(_mnemonic.Text.Trim());
            _writeBtn = new ModernButton("Write", Glyph.Send) { Margin = Padding.Empty };
            _writeBtn.Click += async (s, e) => await ManualWrite();
            _mnemonic.Committed += async (s, e) => { if (_mnemonic.InnerTextBox.Focused) await ManualRead(_mnemonic.Text.Trim()); };

            var manualRow = Flow();
            manualRow.Controls.AddRange([
                new ThemedLabel("Parameter", Theme.Small, Theme.TextDim) { Margin = new Padding(0, Theme.S(9), Theme.S(8), 0) },
                _mnemonic, _value, _readBtn, _writeBtn,
            ]);

            var quick = Flow(FlowDirection.LeftToRight, true);
            quick.Margin = new Padding(0, Theme.S(10), 0, 0);
            quick.Controls.Add(new ThemedLabel("Quick read", Theme.Small, Theme.TextDim) { Margin = new Padding(0, Theme.S(7), Theme.S(8), 0) });
            foreach (var m in new[] { "PV", "SP", "OP", "OS", "SW", "SE", "ch", "Hb", "SL", "r1", "l1", "t1" }) {
                string mn = m;
                var b = new ModernButton(mn, Glyph.None, ButtonKind.Ghost) { Height = Theme.S(28), Margin = new Padding(0, 0, Theme.S(2), 0), Font = Theme.Mono };
                b.FitWidth();
                b.Click += async (s, e) => { _mnemonic.Text = mn; await ManualRead(mn); };
                quick.Controls.Add(b);
            }

            var help = new ThemedLabel(
                "Parameters are case-sensitive two-letter codes. r1–r8 = ramp (or END), l1–l8 = target, t1–t8 = hold, in the controller's own units (Settings → Station); ch = pattern 0–9, " +
                "OS = program control (>0000 stop, >0002 run, >0003 hold). Values are sent exactly as typed.",
                Theme.Small, Theme.TextFaint) { AutoSize = true, MaximumSize = new Size(Theme.S(900), 0), Margin = new Padding(0, Theme.S(10), 0, 0) };

            var manualStack = Flow(FlowDirection.TopDown);
            manualStack.Controls.AddRange([manualRow, quick, help]);
            manualStack.Dock = DockStyle.Fill;

            var manualCard = new CardPanel { Dock = DockStyle.Top, Height = Theme.S(196), Margin = Padding.Empty };
            manualCard.Controls.Add(manualStack);
            manualCard.Controls.Add(new CardHeader("Manual access", "Read or write a single controller parameter"));
            manualStack.BringToFront();

            // ---------------------------------------------------------------- log card
            _log = new RichTextBox {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.Mono,
                WordWrap = false,
                DetectUrls = false,
                HideSelection = false,
            };
            Theme.ApplyDarkScrollbars(_log);

            _showPoll = new ToggleSwitch("Polling") { Margin = new Padding(0, Theme.S(3), Theme.S(12), 0) };
            _showHex = new ToggleSwitch("Hex") { Margin = new Padding(0, Theme.S(3), Theme.S(12), 0), Checked = true };
            _pause = new ToggleSwitch("Pause") { Margin = new Padding(0, Theme.S(3), Theme.S(12), 0) };
            var copy = new ModernButton("", Glyph.Copy) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            copy.Click += (s, e) => {
                if (_plain.Length == 0) return;
                try { Clipboard.SetText(_plain.ToString()); State.SetStatus("Log copied to clipboard"); } catch (Exception ex) { State.SetStatus("Could not copy: " + ex.Message, StatusKind.Error); }
            };
            var save = new ModernButton("", Glyph.Save) { Margin = new Padding(0, 0, Theme.S(6), 0) };
            save.Click += (s, e) => SaveLog();
            var clear = new ModernButton("", Glyph.Trash) { Margin = Padding.Empty };
            clear.Click += (s, e) => { _log.Clear(); _plain.Clear(); _lines = 0; };
            var tip = Theme.CreateToolTip();
            tip.SetToolTip(_showPoll, "Also show the background telemetry polling");
            tip.SetToolTip(copy, "Copy log");
            tip.SetToolTip(save, "Save log to a text file");
            tip.SetToolTip(clear, "Clear log");

            var logHeader = new CardHeader("Serial traffic", "TX = sent to controller, RX = received");
            logHeader.Tools.Controls.AddRange([_showPoll, _showHex, _pause, copy, save, clear]);
            var logCard = new CardPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            logCard.Controls.Add(_log);
            logCard.Controls.Add(logHeader);
            _log.BringToFront();

            var spacer = new Panel { Dock = DockStyle.Top, Height = Theme.S(16), BackColor = Theme.Window };
            Controls.Add(logCard);
            Controls.Add(spacer);
            Controls.Add(manualCard);

            State.Device.Traffic += OnTraffic;
            State.Device.StateChanged += (s, e) => UpdateButtons();
            State.BusyChanged += UpdateButtons;
            UpdateButtons();
            Append(TrafficKind.Info, null, "Console ready. Manual commands appear here; enable “Polling” to also see background telemetry.", false);
        }

        private void UpdateButtons() {
            bool ok = State.Device.IsConnected && !State.Busy;
            _readBtn.Enabled = _writeBtn.Enabled = ok;
        }

        private void OnTraffic(TrafficEntry e) {
            if (_pause.Checked) return;
            if (e.IsPoll && !_showPoll.Checked && e.Kind != TrafficKind.Error) return;
            if (!Visible && _lines > MaxLines) return;
            Append(e.Kind, e.Bytes, e.Text, e.IsPoll, e.Time);
        }

        private void Append(TrafficKind kind, byte[] bytes, string text, bool poll, DateTime? time = null) {
            var t = time ?? DateTime.Now;
            string tag;
            Color color;
            switch (kind) {
                case TrafficKind.Tx: tag = "TX "; color = Theme.Blue; break;
                case TrafficKind.Rx: tag = "RX "; color = Theme.Green; break;
                case TrafficKind.Error: tag = "ERR"; color = Theme.Red; break;
                default: tag = "   "; color = Theme.TextDim; break;
            }
            string body = text ?? "";
            string hex = "";
            if (bytes != null) {
                body = Pc410Protocol.Describe(bytes, 0, bytes.Length);
                if (_showHex.Checked) hex = Pc410Protocol.ToHex(bytes, 0, bytes.Length);
            }

            if (_lines >= MaxLines) TrimLog();

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = Theme.TextFaint;
            _log.AppendText(t.ToString("HH:mm:ss.fff") + "  ");
            _log.SelectionColor = color;
            _log.AppendText(tag + "  ");
            _log.SelectionColor = kind == TrafficKind.Info ? Theme.TextDim : poll ? Theme.TextDim : Theme.Text;
            _log.AppendText(body.PadRight(bytes != null ? 30 : 0));
            if (hex.Length > 0) {
                _log.SelectionColor = Theme.TextFaint;
                _log.AppendText("  " + hex);
            }
            _log.AppendText(Environment.NewLine);
            _log.ScrollToCaret();
            _plain.Append(t.ToString("HH:mm:ss.fff")).Append("  ").Append(tag).Append("  ").Append(body);
            if (hex.Length > 0) _plain.Append("  ").Append(hex);
            _plain.AppendLine();
            _lines++;
        }

        private void TrimLog() {
            int cut = MaxLines / 4;
            int idx = _log.GetFirstCharIndexFromLine(cut);
            if (idx > 0) {
                _log.Select(0, idx);
                _log.ReadOnly = false;
                _log.SelectedText = "";
                _log.ReadOnly = true;
            }
            string all = _plain.ToString();
            int pos = 0;
            for (int i = 0; i < cut && pos >= 0; i++) pos = all.IndexOf('\n', pos + 1);
            if (pos > 0) { _plain.Clear(); _plain.Append(all.Substring(pos + 1)); }
            _lines -= cut;
        }

        private async System.Threading.Tasks.Task ManualRead(string mn) {
            if (mn.Length != 2) { State.SetStatus("Enter a two-letter parameter code", StatusKind.Warning); return; }
            var (ok, v) = await State.RunAsync($"Reading {mn}", (d, p, ct) => d.Client.Read(mn));
            if (ok) {
                Append(TrafficKind.Info, null, $"{mn} = {v}", false);
                _value.Text = v.Trim();
            }
        }

        private async System.Threading.Tasks.Task ManualWrite() {
            string mn = _mnemonic.Text.Trim();
            string v = _value.Text.Trim();
            if (mn.Length != 2) { State.SetStatus("Enter a two-letter parameter code", StatusKind.Warning); return; }
            if (v.Length == 0) { State.SetStatus("Enter a value to write", StatusKind.Warning); return; }
            if (!DarkDialog.Confirm(FindForm(), $"Write {mn} = {v}?", "The value is sent to the controller exactly as typed. Writing the wrong parameter can change controller behaviour.", "Write", "Cancel", true))
                return;
            var (ok, _) = await State.RunAsync($"Writing {mn}", d => d.Client.Write(mn, v));
            if (ok) Append(TrafficKind.Info, null, $"{mn} ← {v}  (accepted)", false);
        }

        private void SaveLog() {
            using var d = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = $"ReflowLink_serial_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                File.WriteAllText(d.FileName, _plain.ToString(), Encoding.UTF8);
                State.SetStatus("Log saved to " + d.FileName, StatusKind.Success);
            } catch (Exception ex) { DarkDialog.Error(FindForm(), "Cannot save log", ex.Message); }
        }
    }
}
