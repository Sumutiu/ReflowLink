// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Windows.Forms;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI {
    /// <summary>Edits a profile's notes (board, paste, nozzle, what worked…). They're saved with the profile file.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class NotesDialog : Form {
        private readonly TextBox _text;

        private NotesDialog(string title, string notes) {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Text = title;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(520), Theme.S(380));

            var hint = new ThemedLabel("Board, solder paste, nozzle, what worked and what didn't. Saved with the profile file (not in the controller).",
                Theme.Small, Theme.TextDim) {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = Theme.S(40),
                Padding = new Padding(0, 0, 0, Theme.S(8)),
            };
            _text = new TextBox {
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Body,
                Text = (notes ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            var body = new Panel { Dock = DockStyle.Fill, Padding = Theme.Pad(20, 16, 20, 12), BackColor = Theme.Surface };
            body.Controls.Add(_text);
            body.Controls.Add(hint);

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
            var ok = new ModernButton("OK", Glyph.None, ButtonKind.Primary) { AutoWidth = false, Width = Theme.S(90), Margin = new Padding(Theme.S(8), 0, 0, 0) };
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var cancel = new ModernButton("Cancel") { AutoWidth = false, Width = Theme.S(90), Margin = new Padding(Theme.S(8), 0, 0, 0) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            flow.Controls.Add(ok);
            flow.Controls.Add(cancel);
            footer.Controls.Add(flow);
            Controls.Add(body);
            Controls.Add(footer);
            CancelButton = cancel;
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        /// <summary>Shows the editor; returns the new notes, or null when cancelled.</summary>
        public static string Edit(IWin32Window owner, string profileName, string notes) {
            using var d = new NotesDialog($"Notes · {profileName}", notes);
            return d.ShowDialog(owner) == DialogResult.OK ? d._text.Text.Replace("\r\n", "\n").TrimEnd() : null;
        }
    }
}
