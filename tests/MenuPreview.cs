using System;
using System.Reflection;
using System.Windows.Forms;

namespace Aevalsistant
{
    // Wine-only harness: drives the real TrayApp with fake hook traffic and opens its menu
    // so the rendering can be screenshotted. Not part of the shipped exe.
    static class MenuPreview
    {
        public static void Run()
        {
            Application.EnableVisualStyles();
            var app = new TrayApp();
            var t = typeof(TrayApp);
            var onMessage = t.GetMethod("OnMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            string Env(string sid, string ev, string cwd, string extra = "") =>
                "{\"t\":0,\"hwnd\":65538,\"pid\":0,\"pidStart\":0,\"raw\":" + Json.Quote(
                    "{\"session_id\":\"" + sid + "\",\"hook_event_name\":\"" + ev + "\",\"cwd\":\"" + cwd + "\"" + extra + "}") + "}";
            onMessage.Invoke(app, new object[] { Env("a", "UserPromptSubmit", "C:\\\\code\\\\aevalrena") });
            onMessage.Invoke(app, new object[] { Env("b", "UserPromptSubmit", "C:\\\\code\\\\kymarion") });
            onMessage.Invoke(app, new object[] { Env("b", "Notification", "C:\\\\code\\\\kymarion", ",\"message\":\"Claude needs your permission to use Bash\",\"notification_type\":\"permission_prompt\"") });
            onMessage.Invoke(app, new object[] { Env("c", "UserPromptSubmit", "C:\\\\code\\\\forge") });
            onMessage.Invoke(app, new object[] { Env("c", "Stop", "C:\\\\code\\\\forge", ",\"last_assistant_message\":\"Done.\"") });
            var menu = (ContextMenuStrip)t.GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
            var timer = new Timer { Interval = 1500 };
            timer.Tick += (s, e) => { timer.Stop(); menu.Show(560, 160); };
            timer.Start();
            Application.Run(app);
        }
    }
}
