using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Aevalsistant.Tests")]

namespace Aevalsistant
{
    static class App
    {
        public static readonly string Home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aevalsistant");
        public static readonly string InstalledExe = Path.Combine(Home, "Aevalsistant.exe");
        public static readonly string SettingsPath = Path.Combine(Home, "settings.ini");
        public static readonly string PipeName = "aevalsistant." + Environment.UserName.ToLowerInvariant();
        public const string MutexName = @"Local\Aevalsistant.Instance";

        [STAThread]
        static int Main(string[] args)
        {
            // --hook [agent] [event]: Claude Code passes no agent; Copilot CLI also passes the event.
            if (args.Length > 0 && args[0] == "--hook")
                return HookClient.Run(args.Length > 1 ? args[1] : "claude", args.Length > 2 ? args[2] : "");

            string self = Process.GetCurrentProcess().MainModule.FileName;
            bool isInstalled = string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

            if (!isInstalled) return InstallAndHandOff(self);

            using (var mutex = new Mutex(true, MutexName, out bool first))
            {
                if (!first) { Send("{\"cmd\":\"hello\"}"); return 0; }
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { /* Windows 10 before 1703: the manifest still applies */ }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // Runs from wherever the user double-clicked it (usually Downloads). Copies itself to
        // %LOCALAPPDATA%\Aevalsistant so the hook path and the startup entry never move.
        static int InstallAndHandOff(string self)
        {
            bool running = Mutex.TryOpenExisting(MutexName, out var existing);
            existing?.Dispose();
            bool same = File.Exists(InstalledExe) && FilesEqual(self, InstalledExe);

            if (running && same) { Send("{\"cmd\":\"hello\"}"); return 0; }
            if (running)
            {
                Send("{\"cmd\":\"quit\"}");
                for (int i = 0; i < 40 && Mutex.TryOpenExisting(MutexName, out var m); i++) { m.Dispose(); Thread.Sleep(100); }
            }
            try
            {
                Directory.CreateDirectory(Home);
                if (!same) File.Copy(self, InstalledExe, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                MessageBox.Show("Could not copy Aevalsistant to " + Home + ".\n\n" + e.Message,
                    "Aevalsistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = Home });
            return 0;
        }

        static bool FilesEqual(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        public static bool Send(string json)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipe.Connect(400);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
                return true;
            }
            catch (TimeoutException) { return false; }   // tray app not running
            catch (IOException) { return false; }        // tray app quit mid-write
        }
    }

    // `Aevalsistant.exe --hook [agent] [event]`, called by a coding agent with its hook payload
    // on stdin. Adds which window and which agent process this came from, forwards it, exits 0.
    static class HookClient
    {
        public static int Run(string agent, string eventName)
        {
            try { return Forward(agent, eventName); }
            finally { Reply(agent); }
        }

        static int Forward(string agent, string eventName)
        {
            // Tray app not running: return at once instead of waiting on a pipe nobody serves.
            if (!Mutex.TryOpenExisting(App.MutexName, out var m)) return 0;
            m.Dispose();
            string raw = ReadStdin(TimeSpan.FromSeconds(2));
            if (raw.Length == 0) return 0;

            long started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            string pwd = Environment.CurrentDirectory;
            string project = "";
            if (Json.TryParse(raw, out var parsed) && parsed is JObj h)
            {
                var probe = new HookEvent { Name = eventName };
                Agents.Normalize(agent, h, probe, pwd);
                project = new Session { Cwd = probe.Cwd }.Project;
            }
            var t = Terminals.FromHookProcess(project, agent);
            string envelope = "{\"t\":" + started
                + ",\"agent\":" + Json.Quote(agent)
                + ",\"event\":" + Json.Quote(eventName)
                + ",\"pwd\":" + Json.Quote(pwd)
                + ",\"host\":" + Json.Quote(t.Host ?? "")
                + ",\"hwnd\":" + t.Window.ToInt64()
                + ",\"pid\":" + t.AgentPid
                + ",\"pidStart\":" + t.AgentStart
                + ",\"raw\":" + Json.Quote(raw) + "}";
            App.Send(envelope);
            return 0;   // never block or fail the agent
        }

        // Cursor reads a JSON answer from every hook; an empty one is reported as a failure.
        // "continue": true lets a prompt through. Other agents read nothing.
        static void Reply(string agent)
        {
            if (agent != "cursor") return;
            try
            {
                using (var stdout = Console.OpenStandardOutput())
                {
                    var bytes = Encoding.UTF8.GetBytes("{\"continue\":true}");
                    stdout.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { /* nobody is reading */ }
        }

        static string ReadStdin(TimeSpan limit)
        {
            string result = "";
            var reader = new Thread(() =>
            {
                try
                {
                    using (var stdin = Console.OpenStandardInput())
                    using (var sr = new StreamReader(stdin, new UTF8Encoding(false)))
                    {
                        var buf = new char[1 << 16];
                        var sb = new StringBuilder();
                        int n;
                        while ((n = sr.Read(buf, 0, buf.Length)) > 0 && sb.Length < (4 << 20)) sb.Append(buf, 0, n);
                        result = sb.ToString();
                    }
                }
                catch (IOException) { /* no stdin attached */ }
            }) { IsBackground = true };
            reader.Start();
            reader.Join(limit);
            return result.Trim();
        }
    }
}
