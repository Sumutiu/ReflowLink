// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ReflowLink.Model;
using ReflowLink.Services;
using ReflowLink.UI.Controls;

namespace ReflowLink.UI.Pages {
    /// <summary>
    /// Backup &amp; Restore: saves ReflowLink's settings and the profile files in one file, or restores them, on this PC or
    /// another one.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Code")]
    public sealed class BackupPage : PageBase {
        private readonly ToggleSwitch _withProfiles;
        private readonly ThemedLabel _profileCount;

        public BackupPage(AppState state)
            : base(state, "Backup & Restore", "Save your setup and profiles to one file, or restore them on this or another PC") {
            // ---------------------------------------------------------------- export
            var export = Card("Export setup", "Saves everything in one .rlbackup file");
            var ef = Body(export);
            ef.Controls.Add(Para("The file holds:", Theme.Body, Theme.Text, 0));
            ef.Controls.Add(Para(
                "• Settings: station, connections, temperature limits, bed heat setup, logging and recording options.\n"
                + "• What ReflowLink keeps for each controller pattern: bed targets, Wait for Bed / Wait for IR, blocked patterns.\n"
                + "• The P, I and D saved before an auto-tune.\n"
                + "• Your profiles (the files in Documents\\ReflowLink\\Profiles), when switched on below.",
                Theme.Small, Theme.TextDim, 4));
            ef.Controls.Add(Para("Folders on this PC aren't included, so the file can be given to someone else.", Theme.Small, Theme.TextFaint, 8));
            _withProfiles = new ToggleSwitch("Include profiles") { Checked = true, Margin = new Padding(0, Theme.S(14), 0, 0) };
            ef.Controls.Add(_withProfiles);
            _profileCount = Para("", Theme.Small, Theme.TextFaint, 4);
            ef.Controls.Add(_profileCount);
            var exportButton = new ModernButton("Export…", Glyph.Upload, ButtonKind.Primary) { Margin = new Padding(0, Theme.S(16), 0, 0) };
            exportButton.Click += (s, e) => Export();
            ef.Controls.Add(exportButton);
            Fit(export, ef);

            // ---------------------------------------------------------------- import
            var import = Card("Import setup", "Restores a backup made here or on another PC");
            var im = Body(import);
            im.Controls.Add(Para("After choosing the file you can import:", Theme.Body, Theme.Text, 0));
            im.Controls.Add(Para(
                "• Everything: your settings are replaced by the backup's and its profiles are added. ReflowLink then restarts. "
                + "Disconnect the controllers first.\n"
                + "• Profiles only: just adds the profiles and keeps your settings. Use this for a backup from another station: "
                + "the pattern data in it belongs to that station's controllers.",
                Theme.Small, Theme.TextDim, 4));
            im.Controls.Add(Para("A profile you already have is left as it is. If a different one has the same name, both are kept and the imported one is "
                + "renamed “… (imported)”. Your current settings are kept as settings.xml.bak.", Theme.Small, Theme.TextFaint, 8));
            var importButton = new ModernButton("Import…", Glyph.Download) { Margin = new Padding(0, Theme.S(16), 0, 0) };
            importButton.Click += (s, e) => Import();
            im.Controls.Add(importButton);
            Fit(import, im);

            // ---------------------------------------------------------------- layout
            var grid = Table(2, 1);
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            export.Dock = import.Dock = DockStyle.Top;
            export.Margin = new Padding(0, 0, Theme.S(8), 0);
            import.Margin = new Padding(Theme.S(8), 0, 0, 0);
            grid.Controls.Add(export, 0, 0);
            grid.Controls.Add(import, 1, 0);
            int height = Math.Max(export.Height, import.Height);
            export.Height = import.Height = height;

            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Window };
            Theme.ApplyDarkScrollbars(scroller);
            scroller.Controls.Add(grid);
            Controls.Add(scroller);
        }

        public override void OnShown() {
            int n = SetupBackup.CountProfiles(AppSettings.ProfilesFolder);
            _profileCount.Text = n == 1 ? "1 profile in your Profiles folder." : $"{n} profiles in your Profiles folder.";
        }

        // ---------------------------------------------------------------- actions

        private void Export() {
            SetupBackup backup;
            try {
                backup = SetupBackup.Create(State.Settings, _withProfiles.Checked ? AppSettings.ProfilesFolder : null);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Couldn't make the backup", ex.Message);
                return;
            }
            string folder = Path.GetDirectoryName(AppSettings.ProfilesFolder);
            using var d = new SaveFileDialog {
                Filter = SetupBackup.FileFilter,
                DefaultExt = SetupBackup.FileExtension,
                FileName = $"ReflowLink-setup-{DateTime.Now:yyyy-MM-dd}{SetupBackup.FileExtension}",
                InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try {
                backup.Save(d.FileName);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Couldn't save the backup", ex.Message);
                return;
            }
            string profiles = backup.Profiles.Count == 1 ? "1 profile" : $"{backup.Profiles.Count} profiles";
            State.SetStatus($"Setup exported with {profiles} to {d.FileName}", StatusKind.Success);
            if (backup.Unreadable.Count > 0)
                DarkDialog.Show(FindForm(), "Some profiles were left out",
                    "These files in the Profiles folder couldn't be read, so they aren't in the backup:\n\n" + string.Join("\n", backup.Unreadable.Take(10)),
                    DialogTone.Warning, ["OK"]);
        }

        private void Import() {
            using var d = new OpenFileDialog { Filter = SetupBackup.FileFilter, InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            SetupBackup backup;
            try {
                backup = SetupBackup.Load(d.FileName);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Can't import this file", ex.Message);
                return;
            }

            var s = backup.Settings;
            string profiles = backup.Profiles.Count == 1 ? "1 profile" : $"{backup.Profiles.Count} profiles";
            string summary = $"Made on {backup.Created:yyyy-MM-dd HH:mm} with ReflowLink {backup.AppVersion}.\n\n"
                + (s != null ? $"Station: {s.Station.Name}\nBed heat: {BedModeText(s.Bed.Mode)}\n" : "No settings in this file.\n")
                + $"Profiles: {profiles}";
            int choice;
            if (s == null) {
                choice = DarkDialog.Show(FindForm(), "Import profiles?", summary, DialogTone.Question, ["Import profiles", "Cancel"], 0) == 0 ? 1 : -1;
            } else {
                choice = DarkDialog.Show(FindForm(), "Import this backup?", summary
                    + "\n\nEverything replaces your settings and adds the profiles, then ReflowLink restarts. Profiles only keeps your settings.",
                    DialogTone.Question, ["Import everything", "Profiles only", "Cancel"], 0);
            }
            if (choice is < 0 or > 1) return;
            bool everything = choice == 0;
            if (everything && (State.Device.IsConnected || State.Bed.IsConnected)) {
                DarkDialog.Error(FindForm(), "Disconnect first", "Disconnect both controllers before importing settings: the station, connections and bed setup are about to change.");
                return;
            }

            ProfileRestoreResult restored;
            try {
                restored = backup.RestoreProfiles(AppSettings.ProfilesFolder);
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Couldn't add the profiles", ex.Message);
                return;
            }
            string profileText = $"Profiles: {restored.Added} added"
                + (restored.Renamed > 0 ? $", {restored.Renamed} added as “(imported)” next to a different one with the same name" : "")
                + (restored.AlreadyThere > 0 ? $", {restored.AlreadyThere} already there" : "") + ".";
            AppLog.Info($"Imported {(everything ? "setup" : "profiles")} from {d.FileName}. {profileText}");
            OnShown();

            if (!everything) {
                State.SetStatus(profileText, StatusKind.Success);
                DarkDialog.Show(FindForm(), "Profiles imported", profileText + "\n\nOpen them on the Profiles page.", DialogTone.Info, ["OK"]);
                return;
            }
            try {
                AppSettings.Install(backup.SettingsForThisPc(State.Settings, AppSettings.ProfilesFolder));
            } catch (Exception ex) {
                DarkDialog.Error(FindForm(), "Couldn't import the settings", $"{profileText}\n\nThe settings couldn't be written ({ex.Message}); your current settings are unchanged.");
                return;
            }
            DarkDialog.Show(FindForm(), "Setup imported", profileText + "\n\nReflowLink restarts now to use the imported settings.", DialogTone.Info, ["Restart"]);
            Application.Restart();
        }

        private static string BedModeText(BedMode mode) => mode switch {
            BedMode.SinglePc410 => "single PC410 (IN2 + OUT2)",
            BedMode.SecondPc410 => "second PC410",
            _ => "off (top IR only)",
        };

        // ---------------------------------------------------------------- builders

        private static CardPanel Card(string title, string subtitle) {
            var c = new CardPanel { Padding = Theme.Pad(20, 16, 20, 20) };
            c.Controls.Add(new CardHeader(title, subtitle));
            return c;
        }

        private static FlowLayoutPanel Body(CardPanel card) {
            var f = new FlowLayoutPanel {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0, Theme.S(6), 0, 0),
            };
            card.Controls.Add(f);
            f.BringToFront();
            return f;
        }

        private static ThemedLabel Para(string text, Font font, Color color, int top) =>
            new(text, font, color) { AutoSize = true, MaximumSize = new Size(Theme.S(440), 0), Margin = new Padding(0, Theme.S(top), 0, 0) };

        private static void Fit(CardPanel card, FlowLayoutPanel body) {
            int h = card.Padding.Vertical;
            foreach (Control c in card.Controls)
                h += c == body ? body.GetPreferredSize(new Size(Theme.S(480), 0)).Height : c.Height;
            card.Height = h + Theme.S(6);
        }
    }
}
