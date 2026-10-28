// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI {
    /// <summary>
    /// Small modal "please wait" window shown while a short task runs (switching the bed off, stopping before exit). The
    /// window keeps the interface responsive, unlike waiting on the task, and can't be closed until the task ends.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class BusyDialog : Form {
        private readonly string _message;
        private readonly Func<Task> _work;
        private bool _done;

        private BusyDialog(string message, Func<Task> work) {
            _message = message;
            _work = work;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.S(340), Theme.S(96));
            var bar = new ModernProgressBar {
                Indeterminate = true,
                Location = new Point(Theme.S(24), Theme.S(62)),
                Size = new Size(Theme.S(340 - 48), Theme.S(6)),
            };
            Controls.Add(bar);
        }

        /// <summary>
        /// Runs <paramref name="work"/> with the window shown and returns its result (or throws its exception). The
        /// window's message loop keeps the interface responsive and lets the task's continuations run.
        /// </summary>
        public static T Run<T>(IWin32Window owner, string message, Func<Task<T>> work) {
            T result = default;
            Exception error = null;
            using (var d = new BusyDialog(message, async () => {
                try { result = await work(); } catch (Exception ex) { error = ex; }
            })) {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                d.ShowDialog(owner);
            }
            if (error != null) throw error;
            return result;
        }

        protected override async void OnShown(EventArgs e) {
            base.OnShown(e);
            try { await _work(); } finally {
                _done = true;
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            if (!_done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var g = e.Graphics;
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            TextRenderer.DrawText(g, _message, Theme.Heading, new Rectangle(Theme.S(24), Theme.S(20), ClientSize.Width - Theme.S(48), Theme.Heading.Height + 4),
                Theme.Text, Theme.LeftMiddle);
        }
    }
}
