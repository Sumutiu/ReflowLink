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

    /// <summary>One PC410 (top IR or bed heat) with the state the pages share about it.</summary>
    public sealed class ControllerChannel(string name, string shortName, DeviceService device, bool isBed) {
        /// <summary>"Top IR" or "Bed Heat".</summary>
        public string Name { get; } = name;
        /// <summary>"IR" or "Bed".</summary>
        public string ShortName { get; } = shortName;
        public DeviceService Device { get; } = device;
        public bool IsBed { get; } = isBed;

        /// <summary>Control settings last read from the controller, or the default values while none have been read.</summary>
        public TuningSnapshot Tuning { get; private set; } = TuningSnapshot.Factory();

        /// <summary>Raised when <see cref="Tuning"/> is replaced.</summary>
        public event Action TuningChanged;

        /// <summary>True while the controller's auto-tune runs (kept up to date by its Control Tuning page).</summary>
        public bool AutoTuneRunning { get; set; }

        public void SetTuning(TuningSnapshot snapshot) {
            Tuning = snapshot ?? TuningSnapshot.Factory();
            TuningChanged?.Invoke();
        }
    }

    /// <summary>Shared application state passed to every page.</summary>
    public sealed class AppState {
        private int _busy;

        public AppState(AppSettings settings, DeviceService device, DeviceService bed) {
            Settings = settings;
            Ir = new ControllerChannel("Top IR", "IR", device, false);
            BedChannel = new ControllerChannel("Bed Heat", "Bed", bed, true);
            // The bed switch handles each poll before the coordinator, so Wait for Bed sees the bed values of the same poll.
            BedSwitch = new BedSwitchController(device, settings) { KnownPattern = slot => slot is >= 0 and <= 9 ? SlotProfiles[slot] : null };
            Sync = new SyncCoordinator(device, bed);
            _blockedSlots = [.. settings.BlockedPatterns];
            ApplyBedMode();
        }

        public AppSettings Settings { get; }
        public ControllerChannel Ir { get; }
        public ControllerChannel BedChannel { get; }
        /// <summary>The top IR controller.</summary>
        public DeviceService Device => Ir.Device;
        /// <summary>The second (bed heat) PC410, only used with <see cref="BedMode.SecondPc410"/>.</summary>
        public DeviceService Bed => BedChannel.Device;
        public SyncCoordinator Sync { get; }
        /// <summary>Single PC410 bed control (IN2 + OUT2), only active with <see cref="BedMode.SinglePc410"/>.</summary>
        public BedSwitchController BedSwitch { get; }
        public Form Owner { get; set; }

        /// <summary>Bed heat hardware chosen in Settings.</summary>
        public BedMode BedMode => Settings.Bed.Mode;

        /// <summary>A bed heater is controlled: bed targets, Wait for Bed, bed readings on the Dashboard.</summary>
        public bool BedEnabled => BedMode != BedMode.None;

        /// <summary>A second PC410 controls the bed: patterns are written to, read from and started on both controllers.</summary>
        public bool DualBed => BedMode == BedMode.SecondPc410;

        /// <summary>The top PC410 reads the bed on IN2 and ReflowLink switches it through OUT2.</summary>
        public bool SingleBed => BedMode == BedMode.SinglePc410;

        /// <summary>"IN2 (Top IR)" is shown as an informative reading (with a single PC410, IN2 is the bed temperature instead).</summary>
        public bool IrIn2Shown => !SingleBed && Settings.ReadIn2;

        /// <summary>"IN2 (Bed Heat)" is shown: the second PC410's second input, information only.</summary>
        public bool BedIn2Shown => DualBed && Settings.Bed.ReadIn2;

        /// <summary>Switches an informative IN2 reading on or off (the Top IR one, or the second PC410's).</summary>
        public void SetIn2Reading(bool bed, bool on) {
            if (bed) Settings.Bed.ReadIn2 = on;
            else Settings.ReadIn2 = on;
            ApplyBedMode();
            Settings.Save();
            BedEnabledChanged?.Invoke();
        }

        /// <summary>Profiles known to be stored in controller patterns 0..9 (read or written this session).</summary>
        public readonly ReflowProfile[] SlotProfiles = new ReflowProfile[10];

        /// <summary>Patterns the controller rejected while being written; they can't be started until rewritten.</summary>
        private readonly HashSet<int> _blockedSlots;

        public event Action SlotProfilesChanged;
        public event Action<string, StatusKind> StatusChanged;
        /// <summary>null = hide, negative = indeterminate, 0..1 = progress.</summary>
        public event Action<double?> ProgressChanged;
        public event Action BusyChanged;
        public event Action<string> NavigateRequested;
        /// <summary>Raised after the station or its controller units change.</summary>
        public event Action StationChanged;
        /// <summary>Raised after the bed heat hardware (<see cref="BedMode"/>) or an IN2 reading is switched.</summary>
        public event Action BedEnabledChanged;

        public bool Busy => _busy > 0;

        /// <summary>True while either controller's auto-tune runs.</summary>
        public bool AutoTuneRunning => Ir.AutoTuneRunning || BedChannel.AutoTuneRunning;

        /// <summary>
        /// Changes the bed heat hardware. Leaving single PC410 control switches OUT2 off first, since nothing would
        /// regulate the bed any more; if that can't be confirmed nothing changes and false is returned, unless
        /// <paramref name="force"/> is set.
        /// </summary>
        public bool SetBedMode(BedMode mode, bool force = false) {
            if (Settings.Bed.Mode == mode) return true;
            if (SingleBed && Device.IsConnected && !BedSwitch.SwitchOffNow() && !force) return false;
            Settings.Bed.Mode = mode;
            Settings.Bed.Enabled = mode == BedMode.SecondPc410;
            ApplyBedMode();
            Settings.Save();
            BedEnabledChanged?.Invoke();
            return true;
        }

        /// <summary>Tells the controllers and services what to read and do for the current bed and IN2 settings.</summary>
        internal void ApplyBedMode() {
            bool single = SingleBed;
            Sync.Enabled = BedEnabled;
            Sync.VirtualBed = single ? BedSwitch.VirtualBed : null;
            BedSwitch.Enabled = single;
            Device.ReadIn2 = single || Settings.ReadIn2;
            Device.ReadAlarm2 = single;
            Bed.ReadIn2 = DualBed && Settings.Bed.ReadIn2;
        }

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

        /// <summary>Call after the generic station's units (or the bed controller's units) were edited.</summary>
        public void ApplyStation() {
            Device.SetUnits(Settings.Units);
            Bed.SetUnits(Settings.Bed.Units);
            Settings.Save();
            StationChanged?.Invoke();
        }

        public bool EnsureConnected() => EnsureConnected(Ir);

        public bool EnsureConnected(ControllerChannel channel) {
            if (channel.Device.IsConnected) return true;
            int r = DarkDialog.Show(Owner, $"{channel.Name} not connected",
                $"Connect to the {channel.Name} controller first (use its Connect button in the sidebar).",
                DialogTone.Info, ["Connection settings", "Close"], 1);
            if (r == 0) Navigate("settings");
            return false;
        }

        /// <summary>Both controllers needed by a pattern operation are connected (the bed only when it's a second PC410).</summary>
        public bool EnsurePairConnected() => EnsureConnected(Ir) && (!DualBed || EnsureConnected(BedChannel));

        /// <summary>Runs an exclusive operation on the top IR controller with busy state, progress and error reporting.</summary>
        public Task<(bool ok, T result)> RunAsync<T>(string label, Func<Pc410Device, IProgress<OperationProgress>, CancellationToken, T> op) =>
            RunOnAsync(Ir, label, op);

        /// <summary>Runs an exclusive operation on one controller.</summary>
        public Task<(bool ok, T result)> RunOnAsync<T>(ControllerChannel channel, string label, Func<Pc410Device, IProgress<OperationProgress>, CancellationToken, T> op) =>
            RunCoreAsync(label, () => EnsureConnected(channel), progress => channel.Device.RunAsync(d => op(d, progress, CancellationToken.None)));

        public Task<(bool ok, bool result)> RunOnAsync(ControllerChannel channel, string label, Action<Pc410Device> op) =>
            RunOnAsync(channel, label, (d, p, ct) => { op(d); return true; });

        /// <summary>
        /// Runs an operation on both controllers at once (both locked, IR first). The bed device is null unless a second
        /// PC410 controls the bed.
        /// </summary>
        public Task<(bool ok, T result)> RunPairAsync<T>(string label, Func<Pc410Device, Pc410Device, IProgress<OperationProgress>, CancellationToken, T> op) =>
            RunCoreAsync(label, EnsurePairConnected, progress =>
                DeviceService.RunBothAsync(Device, DualBed ? Bed : null, (ir, bed) => op(ir, bed, progress, CancellationToken.None)));

        /// <summary>
        /// Stops the program on every connected controller. Unlike <see cref="RunPairAsync{T}"/> it doesn't need both to
        /// be connected, so Stop still works when one of them is offline.
        /// </summary>
        public Task<(bool ok, bool result)> StopAllAsync() =>
            RunCoreAsync("Stopping the program", () => Device.IsConnected || (DualBed && Bed.IsConnected) || EnsureConnected(Ir), async progress => {
                var errors = new List<string>();
                foreach (var ch in new[] { Ir, BedChannel }) {
                    if (!ch.Device.IsConnected || (ch.IsBed && !DualBed)) continue;
                    try { await ch.Device.RunAsync(d => d.Stop()); } catch (Exception ex) { errors.Add($"{ch.Name}: {DeviceService.Describe(ex)}"); }
                }
                if (errors.Count > 0) throw new Pc410Exception("Couldn't stop every controller. " + string.Join(" ", errors));
                return true;
            });

        private async Task<(bool ok, T result)> RunCoreAsync<T>(string label, Func<bool> ensure, Func<IProgress<OperationProgress>, Task<T>> run) {
            if (!ensure()) return (false, default(T));
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
                var r = await run(progress);
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
                Bed.PollNow();
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
