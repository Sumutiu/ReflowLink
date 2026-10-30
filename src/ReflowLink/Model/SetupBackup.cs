// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace ReflowLink.Model {
    /// <summary>A profile file kept in a backup, with the name it had.</summary>
    public sealed class BackupProfile {
        [XmlAttribute("file")] public string FileName { get; set; } = "";
        public ReflowProfile Profile { get; set; }
    }

    /// <summary>What restoring the profiles of a backup did.</summary>
    public sealed class ProfileRestoreResult {
        public int Added, Renamed, AlreadyThere;
    }

    /// <summary>
    /// ReflowLink's settings and the profile files in one file (.rlbackup), to move a setup to another PC or keep a copy.
    /// Paths on this PC (log folder, last opened file) are left out, so a backup can be passed on to someone else.
    /// </summary>
    [XmlRoot("ReflowLinkBackup")]
    public sealed class SetupBackup {
        public const string FileExtension = ".rlbackup";
        public const string FileFilter = "ReflowLink backup (*.rlbackup)|*.rlbackup|All files (*.*)|*.*";
        private static readonly string[] ProfileExtensions = [ReflowProfile.FileExtension, ".ir6profile"];
        private static readonly XmlSerializer Serializer = new(typeof(SetupBackup));

        [XmlAttribute("version")] public int FormatVersion { get; set; } = 1;
        public string AppVersion { get; set; } = "";
        public DateTime Created { get; set; }
        /// <summary>The settings; null in a backup that only holds profiles.</summary>
        public AppSettings Settings { get; set; }
        [XmlArray("Profiles"), XmlArrayItem("Profile")]
        public List<BackupProfile> Profiles { get; set; } = [];

        /// <summary>Profile files in <paramref name="folder"/> that couldn't be read when the backup was made.</summary>
        [XmlIgnore]
        public List<string> Unreadable { get; } = [];

        /// <summary>Makes a backup of <paramref name="settings"/> and, when given, the profile files in <paramref name="profilesFolder"/>.</summary>
        public static SetupBackup Create(AppSettings settings, string profilesFolder) {
            var b = new SetupBackup {
                AppVersion = typeof(SetupBackup).Assembly.GetName().Version.ToString(3),
                Created = DateTime.Now,
                Settings = Copy(settings),
            };
            // Paths belong to this PC (and show the Windows user name): keep only the name of the last profile.
            b.Settings.LogFolder = "";
            b.Settings.LastProfilePath = NameOnly(settings.LastProfilePath);
            if (profilesFolder != null && Directory.Exists(profilesFolder)) {
                foreach (var file in Directory.GetFiles(profilesFolder).Where(IsProfileFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
                    try {
                        b.Profiles.Add(new BackupProfile { FileName = Path.GetFileName(file), Profile = ReflowProfile.Load(file) });
                    } catch {
                        b.Unreadable.Add(Path.GetFileName(file));
                    }
                }
            }
            return b;
        }

        public static bool IsProfileFile(string path) =>
            ProfileExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Number of profile files in a folder (for the Backup page).</summary>
        public static int CountProfiles(string folder) {
            try {
                return Directory.Exists(folder) ? Directory.GetFiles(folder).Count(IsProfileFile) : 0;
            } catch {
                return 0;
            }
        }

        public void Save(string path) =>
            SafeFile.Write(path, stream => {
                using var w = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, IndentChars = "  " });
                Serializer.Serialize(w, this);
            });

        public static SetupBackup Load(string path) {
            SetupBackup b;
            try {
                using var r = XmlReader.Create(path);
                b = (SetupBackup)Serializer.Deserialize(r);
            } catch (InvalidOperationException ex) {
                throw new InvalidDataException("This isn't a ReflowLink backup, or it's damaged (" + (ex.InnerException?.Message ?? ex.Message) + ").");
            }
            b.Profiles = [.. (b.Profiles ?? []).Where(p => p?.Profile != null)];
            b.Settings?.Sanitize();
            return b;
        }

        /// <summary>
        /// Writes the backup's profiles into <paramref name="folder"/>. A file that's already there with the same content is
        /// left alone; one with the same name but other content is kept, and the imported one gets a new name.
        /// </summary>
        public ProfileRestoreResult RestoreProfiles(string folder) {
            Directory.CreateDirectory(folder);
            var result = new ProfileRestoreResult();
            foreach (var item in Profiles) {
                string name = SafeName(item.FileName, item.Profile.Name);
                string target = Path.Combine(folder, name);
                if (File.Exists(target)) {
                    if (SameProfile(target, item.Profile)) {
                        result.AlreadyThere++;
                        continue;
                    }
                    string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                    int n = 1;
                    do {
                        target = Path.Combine(folder, $"{stem} (imported{(n > 1 ? " " + n : "")}){ext}");
                        n++;
                    } while (File.Exists(target));
                    result.Renamed++;
                } else {
                    result.Added++;
                }
                item.Profile.Save(target);
            }
            return result;
        }

        /// <summary>
        /// The backup's settings, made to fit this PC: the window stays where it is here, and the last profile points to
        /// the restored file in this PC's profile folder (if it was in the backup).
        /// </summary>
        public AppSettings SettingsForThisPc(AppSettings current, string profilesFolder) {
            var s = Copy(Settings);
            s.WindowX = current.WindowX;
            s.WindowY = current.WindowY;
            s.WindowWidth = current.WindowWidth;
            s.WindowHeight = current.WindowHeight;
            s.WindowMaximized = current.WindowMaximized;
            s.LogFolder = "";
            string name = NameOnly(s.LastProfilePath);
            string last = name.Length == 0 ? "" : Path.Combine(profilesFolder, name);
            s.LastProfilePath = last.Length > 0 && File.Exists(last) ? last : "";
            return s.Sanitize();
        }

        /// <summary>The file name of a path written on any PC (Windows or not).</summary>
        private static string NameOnly(string path) =>
            string.IsNullOrEmpty(path) ? "" : path.Substring(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1);

        private static string SafeName(string fileName, string profileName) {
            string name = NameOnly(fileName);
            if (string.IsNullOrWhiteSpace(name)) name = (string.IsNullOrWhiteSpace(profileName) ? "profile" : profileName) + ReflowProfile.FileExtension;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return IsProfileFile(name) ? name : name + ReflowProfile.FileExtension;
        }

        private static bool SameProfile(string path, ReflowProfile p) {
            try {
                return Xml(ReflowProfile.Load(path)) == Xml(p);
            } catch {
                return false;
            }
        }

        private static string Xml(ReflowProfile p) {
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb)) new XmlSerializer(typeof(ReflowProfile)).Serialize(w, p);
            return sb.ToString();
        }

        /// <summary>A separate copy of the settings (through the same XML they're saved in).</summary>
        private static AppSettings Copy(AppSettings s) {
            var serializer = new XmlSerializer(typeof(AppSettings));
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb)) serializer.Serialize(w, s);
            using var r = XmlReader.Create(new StringReader(sb.ToString()));
            return (AppSettings)serializer.Deserialize(r);
        }
    }
}
