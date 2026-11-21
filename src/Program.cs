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
        public static readonly string TimePath = Path.Combine(Home, "time.ini");
        public static readonly string PipeName = "aevalsistant." + Environment.UserName.ToLowerInvariant();
        public static readonly string CommandPipeName = PipeName + ".cmd";   // agent commands, answered on the same pipe
        public const string MutexName = @"Local\Aevalsistant.Instance";

        [STAThread]
        static int Main(string[] args)
        {
            // --hook [agent] [event]: Claude Code passes no agent; Copilot CLI also passes the event.
            if (args.Length > 0 && args[0] == "--hook")
                return HookClient.Run(args.Length > 1 ? args[1] : "claude", args.Length > 2 ? args[2] : "");

            // --awake, --sleep, --restart, --shutdown, --notify, --status: run by an agent or by hand.
            if (args.Length > 0 && Array.IndexOf(AgentRequest.Commands, args[0]) >= 0)
                return AgentClient.Run(args);

            if (Native.IsElevatedByUac())
            {
                // An elevated tray app's pipe refuses the hook calls of agents running normally.
                MessageBox.Show("Aevalsistant was started with \"Run as administrator\", so coding agents running normally could not reach it.\n\nStart it again without \"Run as administrator\".",
                    "Aevalsistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

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
            bool running = IsRunning();
            bool same = File.Exists(InstalledExe) && FilesEqual(self, InstalledExe);

            if (running && same) { Send("{\"cmd\":\"hello\"}"); return 0; }
            if (running)
            {
                Send("{\"cmd\":\"quit\"}");
                for (int i = 0; i < 50 && IsRunning(); i++) Thread.Sleep(100);
            }
            try
            {
                Directory.CreateDirectory(Home);
                if (!same) Replace(self);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                MessageBox.Show("Could not copy Aevalsistant to " + Home + ".\n\n" + e.Message,
                    "Aevalsistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // The installed copy quit to make room; start it again rather than leave nothing running.
                if (running && File.Exists(InstalledExe)) Start(InstalledExe);
                return 1;
            }
            return Start(InstalledExe) ? 0 : 1;
        }

        // Windows will not overwrite a running exe, but it will rename one. The copy that just
        // quit, or a --hook call started from it, can hold the file a moment longer, so the old
        // file is moved aside and Updater.CleanUp deletes it on the next start.
        static void Replace(string self)
        {
            string aside = null;
            if (File.Exists(InstalledExe))
            {
                aside = InstalledExe + "." + DateTime.UtcNow.Ticks + ".old";
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(InstalledExe, aside); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(250); }   // antivirus or a hook call has it open
                }
            }
            try { File.Copy(self, InstalledExe); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (aside != null && !File.Exists(InstalledExe)) File.Move(aside, InstalledExe);
                throw;
            }
            // The browser's "downloaded from the internet" mark would follow the copy and bring
            // back the SmartScreen prompt at every sign-in. The user already ran this file.
            Native.DeleteFile(InstalledExe + ":Zone.Identifier");
        }

        static bool Start(string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Home });
                return true;
            }
            catch (System.ComponentModel.Win32Exception e)   // blocked by antivirus or Smart App Control
            {
                MessageBox.Show("Could not start " + exe + ".\n\n" + e.Message, "Aevalsistant", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // An elevated copy's mutex exists but refuses to open for a normal one.
        public static bool IsRunning()
        {
            try
            {
                if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }
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
            catch (TimeoutException) { return false; }              // tray app not running
            catch (IOException) { return false; }                   // tray app quit mid-write
            catch (UnauthorizedAccessException) { return false; }   // tray app running elevated
        }
    }

    // `Aevalsistant.exe --awake 2h "render"` and the other agent commands. Prints the tray app's
    // answer. Exit code 0 when done, 1 when refused or not shown, 2 for bad usage or no tray app.
    static class AgentClient
    {
        const string Usage =
            "Usage: Aevalsistant.exe <command> [--agent <id>]\n"
            + "  --awake <30m|2h|1h30m|on> [reason]   keep the computer awake after your turn ends\n"
            + "  --awake off                          release that hold\n"
            + "  --sleep | --restart | --shutdown     only when the user asks; refused while anything is busy\n"
            + "  --notify <title> [detail] [--needs-you]   show a card\n"
            + "  --status                             what the tray app is doing\n";

        public static int Run(string[] args)
        {
            var req = AgentRequest.Parse(args);
            if (req == null) { Write(Usage, error: true); return 2; }
            if (!App.IsRunning()) { Write("Aevalsistant is not running.\n", error: true); return 2; }
            req.Pwd = Environment.CurrentDirectory;
            try
            {
                var t = Terminals.FromHookProcess(new Session { Cwd = req.Pwd }.Project, req.Agent);
                req.Host = t.Host ?? "";
                req.Hwnd = t.Window.ToInt64();
                req.Pid = t.AgentPid;
                req.PidStart = t.AgentStart;
            }
            catch (Exception) { /* the walk is a nicety: the folder still finds the session */ }
            string reply = CommandPipe.Ask(App.CommandPipeName, req.ToJson());
            if (reply == null) { Write("Aevalsistant did not answer.\n", error: true); return 2; }
            Write(reply.EndsWith("\n", StringComparison.Ordinal) ? reply : reply + "\n", error: false);
            return reply.StartsWith("Refused", StringComparison.Ordinal) || reply.StartsWith("Not shown", StringComparison.Ordinal) ? 1 : 0;
        }

        static void Write(string text, bool error)
        {
            try
            {
                using (var s = error ? Console.OpenStandardError() : Console.OpenStandardOutput())
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { /* nobody is reading */ }
        }
    }

    // The agent command pipe: one message in, one message back.
    static class CommandPipe
    {
        public static string Ask(string name, string json, int timeoutMs = 2000)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    pipe.ReadMode = PipeTransmissionMode.Message;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    var ms = new MemoryStream();
                    var buf = new byte[4096];
                    do
                    {
                        int n = pipe.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                    } while (!pipe.IsMessageComplete);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Waits for one client, reads its message, and answers with handle's reply.
        public static void ServeOne(string name, Func<string, string> handle)
        {
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.None))
            {
                server.WaitForConnection();
                var ms = new MemoryStream();
                var buf = new byte[1 << 16];
                do
                {
                    int n = server.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                } while (!server.IsMessageComplete && ms.Length < (1 << 20));
                var bytes = Encoding.UTF8.GetBytes(handle(Encoding.UTF8.GetString(ms.ToArray())));
                server.Write(bytes, 0, bytes.Length);
                server.Flush();
                server.WaitForPipeDrain();
            }
        }
    }

    // `Aevalsistant.exe --hook [agent] [event]`, called by a coding agent with its hook payload
    // on stdin. Adds which window and which agent process this came from, forwards it, exits 0.
    static class HookClient
    {
        public static int Run(string agent, string eventName)
        {
            try { Forward(agent, eventName); }
            // A hook must never fail the agent that ran it. Whatever went wrong here (a process
            // that exited mid-walk, a payload shape nobody expected), the agent carries on.
            catch (Exception) { }
            finally { Reply(agent); }
            return 0;
        }

        static void Forward(string agent, string eventName)
        {
            // Tray app not running: return at once instead of waiting on a pipe nobody serves.
            if (!App.IsRunning()) return;
            string raw = ReadStdin(TimeSpan.FromSeconds(2));
            if (raw.Length == 0) return;

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
                + ",\"outer\":" + t.OuterPid
                + ",\"outerStart\":" + t.OuterStart
                + ",\"raw\":" + Json.Quote(raw) + "}";
            App.Send(envelope);
        }

        // Cursor reads a JSON answer from its hooks: beforeSubmitPrompt takes "continue": true to
        // let the prompt through, and the others ignore it. Other agents read nothing.
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
