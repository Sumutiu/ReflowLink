// ReflowLink
// Copyright (c) 2026 Marius Sumutiu. All rights reserved.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace ReflowLink.Services {
    /// <summary>A release newer than the running version.</summary>
    public sealed class UpdateInfo {
        /// <summary>Version from the release tag (three numbers, e.g. 2.1.2).</summary>
        public Version Version { get; set; }
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Release page on GitHub, where the exe is downloaded.</summary>
        public string Url { get; set; } = "";
        /// <summary>Release notes (Markdown, as written on GitHub).</summary>
        public string Notes { get; set; } = "";
    }

    /// <summary>
    /// Asks GitHub for the latest release of ReflowLink and compares its tag (v2.1.2) with the running version (2.1.1.0;
    /// only the first three numbers count). Nothing is downloaded or installed: the user is sent to the release page.
    /// Only GitHub's public release information is requested; nothing about the user or the station is sent.
    /// </summary>
    public static class UpdateChecker {
        public const string Owner = "Sumutiu", Repository = "ReflowLink";
        public static readonly string ReleasesPage = $"https://github.com/{Owner}/{Repository}/releases";
        private static readonly string LatestApi = $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

        /// <summary>The running version, as three numbers (2.1.1.0 becomes 2.1.1).</summary>
        public static Version Current => Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

        /// <summary>
        /// The latest release if it's newer than this version, otherwise null. Drafts and pre-releases are never offered
        /// (GitHub's "latest" leaves them out). Throws when GitHub can't be reached.
        /// </summary>
        public static Task<UpdateInfo> CheckAsync() => Task.Run(() => {
            string json = Download(LatestApi);
            if (json == null) return null; // no release published yet
            var latest = Parse(json);
            return latest != null && latest.Version > Current ? latest : null;
        });

        private static string Download(string url) {
            // .NET Framework 4.8 uses what Windows offers; only add TLS 1.2 when an older fixed setting is in force.
            if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = $"ReflowLink/{Current}";
            request.Accept = "application/vnd.github+json";
            request.Timeout = request.ReadWriteTimeout = 10000;
            try {
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
                return reader.ReadToEnd();
            } catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound) {
                return null;
            }
        }

        /// <summary>Reads GitHub's release JSON. Null when the tag isn't a version, or the link isn't this repository's.</summary>
        internal static UpdateInfo Parse(string json) {
            Release r;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                r = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(ms);
            if (r == null || r.Draft || r.Prerelease) return null;
            var version = ParseTag(r.Tag);
            if (version == null) return null;
            // Only ever open a page of this repository, whatever the reply says.
            string url = r.Url != null && r.Url.StartsWith(ReleasesPage + "/", StringComparison.OrdinalIgnoreCase) ? r.Url : ReleasesPage;
            return new UpdateInfo { Version = version, Tag = r.Tag, Name = r.Name ?? "", Url = url, Notes = r.Body ?? "" };
        }

        /// <summary>"v2.1.2" or "2.1.2" to 2.1.2; null when the tag isn't a version.</summary>
        public static Version ParseTag(string tag) {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int dash = t.IndexOfAny(['-', '+', ' ']);
            if (dash >= 0) t = t.Substring(0, dash);
            return Version.TryParse(t.Contains('.') ? t : t + ".0", out var v) ? Normalize(v) : null;
        }

        /// <summary>Keeps major, minor and build, so 2.1.1.0 and 2.1.1 compare as equal.</summary>
        public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>The start of the release notes as plain text, for the notification.</summary>
        public static string Summary(string notes, int maxLines = 8, int maxChars = 500) {
            if (string.IsNullOrWhiteSpace(notes)) return "";
            var lines = notes.Replace("\r", "").Split('\n')
                .Where(l => !(l.TrimStart().StartsWith("#") && l.Contains("ReflowLink"))) // the release title repeats the version
                .Select(l => l.Trim().TrimStart('#').Trim().Replace("**", "").Replace("`", ""))
                .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "• " + l.Substring(2) : l)
                .Where(l => l.Length > 0)
                .ToList();
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var l in lines) {
                if (shown == maxLines || sb.Length + l.Length > maxChars) {
                    sb.Append("…");
                    break;
                }
                sb.AppendLine(l);
                shown++;
            }
            return sb.ToString().TrimEnd();
        }

        [DataContract]
        private sealed class Release {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "html_url")] public string Url { get; set; }
            [DataMember(Name = "body")] public string Body { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        }
    }
}
