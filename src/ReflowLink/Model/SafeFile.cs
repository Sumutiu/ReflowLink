// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.IO;

namespace ReflowLink.Model {
    /// <summary>
    /// Writes files so that a crash, a full disk or a power cut while saving never leaves a half-written file: the content
    /// goes to a temporary file first, which then replaces the old one in a single step.
    /// </summary>
    public static class SafeFile {
        /// <summary>Writes a file through <paramref name="write"/>, then swaps it in. The previous version is kept as <paramref name="backupPath"/> when given.</summary>
        public static void Write(string path, Action<Stream> write, string backupPath = null) {
            if (write == null) throw new ArgumentNullException(nameof(write));
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = path + ".tmp";
            try {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    write(fs);
                    fs.Flush(true);
                }
                Replace(temp, path, backupPath);
            } catch {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static void Replace(string temp, string path, string backupPath) {
            if (!File.Exists(path)) {
                File.Move(temp, path);
                return;
            }
            try {
                File.Replace(temp, path, backupPath, true);
                return;
            } catch (PlatformNotSupportedException) {
            } catch (IOException) when (File.Exists(temp) && File.Exists(path)) {
                // Some file systems (network shares) don't support Replace: fall back to copy and move.
            }
            if (backupPath != null) File.Copy(path, backupPath, true);
            File.Delete(path);
            File.Move(temp, path);
        }
    }
}
