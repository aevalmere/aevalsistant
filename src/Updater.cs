using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace Aevalsistant
{
    // Updates come from the latest GitHub release of this repository. The release carries one
    // asset, Aevalsistant.exe, published by .github/workflows/release.yml. GitHub reports each
    // asset's SHA-256 as its "digest", and a download that does not match it is thrown away. The
    // repository has to be public: the check uses GitHub's API without signing in.
    static class Updater
    {
        public const string Repo = "aevalmere/aevalsistant";
        const string Asset = "Aevalsistant.exe";
        public static readonly string Folder = Path.Combine(App.Home, "update");
        internal static string ApiBase = "https://api.github.com";   // tests point this at a local server

        public static Version Current => Assembly.GetExecutingAssembly().GetName().Version;

        public sealed class Result
        {
            public Version Latest;
            public string Downloaded;   // path of a verified newer exe, or null
            public string Problem;      // short reason the check failed, or null
        }

        // Blocking; run it off the UI thread.
        public static Result Check()
        {
            var r = new Result();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string json;
                using (var web = Client(api: true)) json = web.DownloadString(ApiBase + "/repos/" + Repo + "/releases/latest");
                var release = Json.Parse(json) as JObj;
                r.Latest = ParseTag(release?.Str("tag_name"));
                if (r.Latest == null) { r.Problem = "The latest release has no version tag"; return r; }
                if (r.Latest <= Current) return r;

                if (!FindAsset(release, out string url, out string expected)) { r.Problem = "Release " + r.Latest.ToString(3) + " has no " + Asset; return r; }
                if (expected == null) { r.Problem = "Release " + r.Latest.ToString(3) + " has no checksum"; return r; }

                Directory.CreateDirectory(Folder);
                string part = Path.Combine(Folder, Asset + ".part"), target = Path.Combine(Folder, Asset);
                using (var web = Client()) web.DownloadFile(url, part);

                if (Sha256(part) != expected) { File.Delete(part); r.Problem = "Download did not match its checksum"; return r; }
                // The file must be this app, at the version the release claims.
                var name = AssemblyName.GetAssemblyName(part);
                if (name.Name != "Aevalsistant" || name.Version < r.Latest) { File.Delete(part); r.Problem = "Downloaded file is not the expected build"; return r; }

                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                r.Downloaded = target;
            }
            catch (WebException e) { r.Problem = e.Response is HttpWebResponse h && h.StatusCode == HttpStatusCode.NotFound ? "No releases published yet" : "Offline or GitHub unreachable"; }
            catch (IOException e) { r.Problem = "Could not save the update: " + e.Message; }
            catch (UnauthorizedAccessException) { r.Problem = "No permission to save the update"; }
            catch (BadImageFormatException) { r.Problem = "Downloaded file is not a valid program"; }
            catch (FormatException) { r.Problem = "GitHub sent an unreadable answer"; }
            return r;
        }

        // The exe's download URL and its lowercase SHA-256 from the release's asset list. The hash
        // is null when GitHub gave no "sha256:" digest.
        internal static bool FindAsset(JObj release, out string url, out string sha256)
        {
            url = sha256 = null;
            if (!(release?["assets"] is System.Collections.Generic.List<object> assets)) return false;
            foreach (var a in assets)
            {
                var o = a as JObj;
                if (o?.Str("name") != Asset) continue;
                url = o.Str("browser_download_url");
                string digest = o.Str("digest") ?? "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 7 + 64)
                    sha256 = digest.Substring(7).ToLowerInvariant();
                return url != null;
            }
            return false;
        }

        // "v1.2.0" -> 1.2.0
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.TrimStart('v', 'V');
            if (!Version.TryParse(t.Contains(".") ? t : t + ".0", out var v)) return null;
            // Normalize to four parts so 1.2.0 and 1.2.0.0 compare equal.
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        // Start the downloaded exe. It runs the normal install hand-off: asks this copy to quit,
        // copies itself over the installed exe, and starts it.
        // Returns null, or why the update could not start.
        public static string Apply(string downloaded)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloaded) { UseShellExecute = false, WorkingDirectory = App.Home });
                return null;
            }
            catch (System.ComponentModel.Win32Exception e) { return e.Message; }   // blocked by antivirus or Smart App Control
        }

        // Left over from the previous update once the installed copy is running: the download
        // folder, and the old exe that the hand-off moved aside.
        public static void CleanUp()
        {
            try
            {
                if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
                foreach (var old in Directory.GetFiles(App.Home, "Aevalsistant.exe.*.old")) File.Delete(old);
            }
            catch (IOException) { /* still in use; next start tries again */ }
            catch (UnauthorizedAccessException) { }
        }

        static WebClient Client(bool api = false)
        {
            var web = new WebClient();
            web.Headers[HttpRequestHeader.UserAgent] = "Aevalsistant/" + Current;   // GitHub rejects requests without one
            if (api) web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return web;
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }
    }
}
