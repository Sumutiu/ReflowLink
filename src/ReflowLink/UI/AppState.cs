// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Protocol;
using ReflowLink.Services;

namespace ReflowLink.UI {
    public enum StatusKind { Normal, Success, Warning, Error }

    /// <summary>Shared application state passed to every page.</summary>
    public sealed class AppState(AppSettings settings, DeviceService device) {
        private int _busy;

        public AppSettings Settings { get; } = settings;
        public DeviceService Device { get; } = device;
        public Form Owner { get; set; }

        /// <summary>Profiles known to be stored in controller patterns 0..9 (read or written this session).</summary>
        public readonly ReflowProfile[] SlotProfiles = new ReflowProfile[10];

        /// <summary>Patterns the controller rejected while being written; they can't be started until rewritten.</summary>
        private readonly HashSet<int> _blockedSlots = [.. settings.BlockedPatterns];

        public event Action SlotProfilesChanged;
        public event Action<string, StatusKind> StatusChanged;
        /// <summary>null = hide, negative = indeterminate, 0..1 = progress.</summary>
        public event Action<double?> ProgressChanged;
        public event Action BusyChanged;
        public event Action<string> NavigateRequested;
        /// <summary>Raised after the station or its controller units change.</summary>
        public event Action StationChanged;

        public bool Busy => _busy > 0;

        public void SetSlotProfile(int slot, ReflowProfile p) {
            if (slot is < 0 or > 9) return;
            SlotProfiles[slot] = p?.Clone();
            SlotProfilesChanged?.Invoke();
        }

        public bool IsSlotBlocked(int slot) => _blockedSlots.Contains(slot);

        /// <summary>Marks a pattern as unusable after the controller rejected part of a profile.</summary>
        public void BlockSlot(int slot) {
            if (slot is < 0 or > 9) return;
            _blockedSlots.Add(slot);
            SlotProfiles[slot] = null;
            SaveBlockedSlots();
            SlotProfilesChanged?.Invoke();
        }

        public void UnblockSlot(int slot) {
            if (!_blockedSlots.Remove(slot)) return;
            SaveBlockedSlots();
            SlotProfilesChanged?.Invoke();
        }

        /// <summary>Blocks survive a restart: a partly written pattern stays unusable until a valid profile is written.</summary>
        private void SaveBlockedSlots() {
            Settings.BlockedPatterns = [.. _blockedSlots.OrderBy(s => s)];
            Settings.Save();
        }

        public string SlotLabel(int slot) {
            if (_blockedSlots.Contains(slot)) return $"Pattern {slot}  ·  rejected, write a valid profile";
            var p = SlotProfiles[slot];
            if (p == null) return $"Pattern {slot}";
            string name = string.IsNullOrWhiteSpace(p.Name) ? $"{p.Steps.Count} step(s)" : p.Name;
            return $"Pattern {slot}  ·  {name}";
        }

        public IEnumerable<string> SlotLabels() {
            for (int i = 0; i < 10; i++) yield return SlotLabel(i);
        }

        public void SetStatus(string message, StatusKind kind = StatusKind.Normal) => StatusChanged?.Invoke(message, kind);

        public void Navigate(string page) => NavigateRequested?.Invoke(page);

        /// <summary>Selects a station: applies its temperature limit and units, saves, and notifies the UI.</summary>
        public void ChangeStation(string id) {
            Settings.SelectStation(id);
            ApplyStation();
        }

        /// <summary>Call after the generic station's units were edited.</summary>
        public void ApplyStation() {
            Device.SetUnits(Settings.Units);
            Settings.Save();
            StationChanged?.Invoke();
        }

        public bool EnsureConnected() {
            if (Device.IsConnected) return true;
            int r = DarkDialog.Show(Owner, "Not connected", "Connect to the controller first (use the Connect button in the sidebar).",
                DialogTone.Info, ["Connect settings", "Close"], 1);
            if (r == 0) Navigate("settings");
            return false;
        }

        /// <summary>Runs an exclusive device operation with busy state, progress and error reporting.</summary>
        public async Task<(bool ok, T result)> RunAsync<T>(string label, Func<Pc410Device, IProgress<OperationProgress>, CancellationToken, T> op) {
            if (!EnsureConnected()) return (false, default(T));
            if (Busy) {
                SetStatus("Another operation is still running…", StatusKind.Warning);
                return (false, default(T));
            }
            _busy++;
            BusyChanged?.Invoke();
            ProgressChanged?.Invoke(-1);
            SetStatus(label + "…");
            var progress = new Progress<OperationProgress>(p => {
                ProgressChanged?.Invoke(p.Total > 0 ? p.Done / (double)p.Total : -1);
                if (!string.IsNullOrEmpty(p.Text)) SetStatus(p.Text);
            });
            try {
                var r = await Device.RunAsync(d => op(d, progress, CancellationToken.None));
                SetStatus(label + " — done", StatusKind.Success);
                return (true, r);
            } catch (ProfileRejectedException ex) {
                if (ex.PatternChanged) BlockSlot(ex.Slot);
                string title = ex.PatternChanged ? "Profile rejected by the controller" : label + " refused";
                SetStatus(title, StatusKind.Error);
                DarkDialog.Error(Owner, title, ex.Message);
                return (false, default(T));
            } catch (Exception ex) {
                string msg = ex is Pc410Exception or InvalidOperationException ? DeviceService.Describe(ex) : ex.Message;
                SetStatus(label + " failed: " + msg, StatusKind.Error);
                DarkDialog.Error(Owner, label + " failed", msg);
                return (false, default(T));
            } finally {
                _busy--;
                ProgressChanged?.Invoke(null);
                BusyChanged?.Invoke();
                Device.PollNow();
            }
        }

        public Task<(bool ok, bool result)> RunAsync(string label, Action<Pc410Device> op) =>
            RunAsync(label, (d, p, ct) => { op(d); return true; });

        public static Color StatusColor(StatusKind k) {
            return k switch {
                StatusKind.Success => Theme.Green,
                StatusKind.Warning => Theme.Yellow,
                StatusKind.Error => Theme.Red,
                _ => Theme.TextDim,
            };
        }
    }

    /// <summary>Base class for the main content pages.</summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public class PageBase : UserControl {
        public PageBase(AppState state, string title, string subtitle) {
            State = state;
            Title = title;
            Subtitle = subtitle;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
        }

        protected AppState State { get; }
        public string Title { get; }
        public string Subtitle { get; }

        public virtual void OnShown() { }

        /// <summary>Lets a page handle keyboard shortcuts (Ctrl+S…) while it is visible.</summary>
        public virtual bool HandleShortcut(Keys keys) => false;

        // ---- small layout helpers shared by pages

        protected static TableLayoutPanel Table(int columns, int rows) {
            var t = new TableLayoutPanel {
                ColumnCount = columns,
                RowCount = rows,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            return t;
        }

        protected static FlowLayoutPanel Flow(FlowDirection dir = FlowDirection.LeftToRight, bool wrap = false) {
            return new FlowLayoutPanel {
                FlowDirection = dir,
                WrapContents = wrap,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
        }
    }
}
