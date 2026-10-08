using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace Aevalsistant
{
    // Updates come from the latest GitHub release of this repository. The release must carry an
    // asset named Aevalsistant.exe, and may carry Aevalsistant.exe.sha256; the release workflow in
    // .github/workflows/release.yml publishes both. The repository has to be public: the check
    // uses GitHub's API without signing in.
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

                string url = null, sumUrl = null;
                if (release["assets"] is System.Collections.Generic.List<object> assets)
                    foreach (var a in assets)
                    {
                        var o = a as JObj;
                        if (o?.Str("name") == Asset) url = o.Str("browser_download_url");
                        if (o?.Str("name") == Asset + ".sha256") sumUrl = o.Str("browser_download_url");
                    }
                if (url == null) { r.Problem = "Release " + r.Latest + " has no " + Asset; return r; }

                Directory.CreateDirectory(Folder);
                string part = Path.Combine(Folder, Asset + ".part"), target = Path.Combine(Folder, Asset);
                using (var web = Client()) web.DownloadFile(url, part);

                if (sumUrl != null)
                {
                    string expected;
                    using (var web = Client()) expected = web.DownloadString(sumUrl).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                    if (Sha256(part) != expected) { File.Delete(part); r.Problem = "Download did not match its checksum"; return r; }
                }
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
        public static void Apply(string downloaded) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloaded) { UseShellExecute = false, WorkingDirectory = App.Home });

        // Left over from the previous update once the installed copy is running.
        public static void CleanUp()
        {
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
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
