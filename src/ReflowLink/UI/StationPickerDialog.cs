// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI {
    /// <summary>First-run dialog: choose the station the PC410 is installed in.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class StationPickerDialog : Form {
        private readonly List<StationOption> _options = [];
        private int _selected;

        private StationPickerDialog(string selectedId) {
            Text = "ReflowLink";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;

            int pad = Theme.S(24);
            int width = Theme.S(560);
            int top = pad + Theme.Heading.Height + Theme.S(8) + Theme.Body.Height * 2 + Theme.S(20);
            int optionHeight = Theme.S(70);
            _selected = StationCatalog.IndexOf(selectedId);

            for (int i = 0; i < StationCatalog.All.Length; i++) {
                int index = i;
                var station = StationCatalog.All[i];
                var option = new StationOption(station.Name, station.Controls) {
                    Bounds = new Rectangle(pad, top + i * (optionHeight + Theme.S(8)), width - pad * 2, optionHeight),
                    Selected = i == _selected,
                };
                option.Click += (s, e) => Select(index);
                option.DoubleClick += (s, e) => { Select(index); Close(); };
                _options.Add(option);
                Controls.Add(option);
            }

            int footerTop = top + StationCatalog.All.Length * (optionHeight + Theme.S(8)) + Theme.S(8);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Theme.Sidebar };
            var ok = new ModernButton("Continue", Glyph.None, ButtonKind.Primary) { AutoWidth = false, Width = Theme.S(120) };
            ok.Location = new Point(width - pad - ok.Width, Theme.S(15));
            ok.Click += (s, e) => Close();
            footer.Controls.Add(ok);
            Controls.Add(footer);
            AcceptButton = ok;
            ClientSize = new Size(width, footerTop + footer.Height);

            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e) {
            switch (e.KeyCode) {
                case Keys.Down:
                    Select(Math.Min(_options.Count - 1, _selected + 1));
                    e.Handled = true;
                    break;
                case Keys.Up:
                    Select(Math.Max(0, _selected - 1));
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    Close();
                    break;
            }
        }

        private void Select(int index) {
            _selected = index;
            for (int i = 0; i < _options.Count; i++) _options[i].Selected = i == index;
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            Theme.ApplyDarkTitleBar(this);
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            int pad = Theme.S(24);
            int width = ClientSize.Width - pad * 2;
            TextRenderer.DrawText(e.Graphics, "Which station do you have?", Theme.Heading,
                new Rectangle(pad, pad, width, Theme.Heading.Height + 4), Theme.Text, Theme.LeftMiddle);
            TextRenderer.DrawText(e.Graphics,
                "ReflowLink matches the controller's units and temperature limit to your station. You can change this later in Settings.",
                Theme.Body, new Rectangle(pad, pad + Theme.Heading.Height + Theme.S(8), width, Theme.Body.Height * 2 + Theme.S(4)),
                Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        /// <summary>Shows the picker and returns the chosen station id.</summary>
        public static string Pick(IWin32Window owner, string selectedId) {
            using var d = new StationPickerDialog(selectedId);
            if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
            d.ShowDialog(owner);
            return StationCatalog.All[d._selected].Id;
        }

        /// <summary>Selectable card with a radio mark, a title and a description.</summary>
        [System.ComponentModel.DesignerCategory("Code")]
        private sealed class StationOption : ThemedControl {
            private readonly string _detail;
            private bool _selected, _hover;

            public StationOption(string title, string detail) {
                Text = title;
                _detail = detail;
                Cursor = Cursors.Hand;
            }

            public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e) {
                var g = e.Graphics;
                Theme.HighQuality(g);
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (var path = Theme.Rounded(r, Theme.Sf(8))) {
                    using var fill = new SolidBrush(_selected ? Theme.SurfaceRaised : _hover ? Theme.Input : Theme.Surface);
                    using var border = new Pen(_selected ? Theme.Accent : Theme.Border, _selected ? Theme.Sf(1.5f) : 1f);
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                float d = Theme.Sf(16);
                float cx = Theme.Sf(16), cy = (Height - d) / 2f;
                using var ring = new Pen(_selected ? Theme.Accent : Theme.BorderStrong, Theme.Sf(1.5f));
                g.DrawEllipse(ring, cx, cy, d, d);
                if (_selected) {
                    using var dot = new SolidBrush(Theme.Accent);
                    float inner = d * 0.5f;
                    g.FillEllipse(dot, cx + (d - inner) / 2, cy + (d - inner) / 2, inner, inner);
                }

                int x = Theme.S(46);
                int w = Width - x - Theme.S(14);
                TextRenderer.DrawText(g, Text, Theme.BodyBold, new Rectangle(x, Theme.S(10), w, Theme.BodyBold.Height + 2), Theme.Text, Theme.LeftMiddle);
                TextRenderer.DrawText(g, _detail, Theme.Small, new Rectangle(x, Theme.S(12) + Theme.BodyBold.Height, w, Height - Theme.S(14) - Theme.BodyBold.Height),
                    Theme.TextDim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
