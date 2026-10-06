// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Windows.Forms;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI {
    public enum DialogTone { Info, Warning, Error, Question }

    /// <summary>Themed replacement for MessageBox.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class DarkDialog : Form {
        private readonly DialogTone _tone;
        private readonly string _title, _message;

        private DarkDialog(string title, string message, DialogTone tone, string[] buttons, int defaultIndex, ButtonKind primaryKind) {
            _tone = tone;
            _title = title;
            _message = message;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Text = "ReflowLink";
            AutoScaleMode = AutoScaleMode.None;

            int width = Theme.S(460);
            int textW = width - Theme.S(24 + 32 + 16 + 24);
            var msgSize = TextRenderer.MeasureText(message ?? "", Theme.Body, new Size(textW, 0), TextFormatFlags.WordBreak);
            int bodyH = Theme.S(24) + Theme.Heading.Height + Theme.S(8) + msgSize.Height + Theme.S(24);
            ClientSize = new Size(width, Math.Max(Theme.S(150), bodyH + Theme.S(64)));

            var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Theme.Sidebar };
            var flow = new FlowLayoutPanel {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, Theme.S(15), Theme.S(16), 0),
                BackColor = Theme.Sidebar,
            };
            for (int i = buttons.Length - 1; i >= 0; i--) {
                int idx = i;
                var b = new ModernButton(buttons[i], Glyph.None, i == defaultIndex ? primaryKind : ButtonKind.Secondary) {
                    Margin = new Padding(Theme.S(8), 0, 0, 0),
                };
                if (b.Width < Theme.S(90)) { b.AutoWidth = false; b.Width = Theme.S(90); }
                b.Click += (s, e) => { Result = idx; DialogResult = DialogResult.OK; Close(); };
                flow.Controls.Add(b);
                if (i == defaultIndex) AcceptButton = b;
            }
            footer.Controls.Add(flow);
            Controls.Add(footer);
            Result = -1;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Result = buttons.Length > 1 ? buttons.Length - 1 : 0; Close(); } };
        }

        public int Result { get; private set; }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var g = e.Graphics;
            Theme.HighQuality(g);
            Color c; Glyph glyph;
            switch (_tone) {
                case DialogTone.Warning: c = Theme.Yellow; glyph = Glyph.Warning; break;
                case DialogTone.Error: c = Theme.Red; glyph = Glyph.Warning; break;
                case DialogTone.Question: c = Theme.Accent; glyph = Glyph.Info; break;
                default: c = Theme.Blue; glyph = Glyph.Info; break;
            }
            int pad = Theme.S(24);
            int icon = Theme.S(32);
            using (var b = new SolidBrush(Theme.WithAlpha(c, 36))) g.FillEllipse(b, pad, pad, icon, icon);
            IconPainter.Draw(g, glyph, new RectangleF(pad + Theme.S(7), pad + Theme.S(7), icon - Theme.S(14), icon - Theme.S(14)), c);
            int tx = pad + icon + Theme.S(16);
            int tw = ClientSize.Width - tx - pad;
            TextRenderer.DrawText(g, _title, Theme.Heading, new Rectangle(tx, pad, tw, Theme.Heading.Height + 4), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(g, _message, Theme.Body, new Rectangle(tx, pad + Theme.Heading.Height + Theme.S(8), tw, ClientSize.Height), Theme.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top);
        }

        public static int Show(IWin32Window owner, string title, string message, DialogTone tone, string[] buttons, int defaultIndex = 0, ButtonKind primary = ButtonKind.Primary) {
            using var d = new DarkDialog(title, message, tone, buttons, defaultIndex, primary);
            if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
            d.ShowDialog(owner);
            return d.Result;
        }

        public static void Error(IWin32Window owner, string title, string message) =>
            Show(owner, title, message, DialogTone.Error, ["OK"]);

        public static bool Confirm(IWin32Window owner, string title, string message, string yes = "Continue", string no = "Cancel", bool danger = false) {
            if (yes is null) {
                throw new ArgumentNullException(nameof(yes));
            }

            if (no is null) {
                throw new ArgumentNullException(nameof(no));
            }

            return Show(owner, title, message, danger ? DialogTone.Warning : DialogTone.Question, [yes, no], 0, danger ? ButtonKind.Danger : ButtonKind.Primary) == 0;
        }
    }
}
