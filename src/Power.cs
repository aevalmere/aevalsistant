using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using static Aevalsistant.Native;

namespace Aevalsistant
{
    // A named power request, visible to the user in `powercfg /requests` as
    // "Aevalsistant: AI agents are running".
    sealed class AwakeRequest : IDisposable
    {
        readonly IntPtr handle;
        bool system, display;

        public AwakeRequest()
        {
            var ctx = new REASON_CONTEXT
            {
                Version = 0, Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "Aevalsistant: AI agents are running",
            };
            handle = PowerCreateRequest(ref ctx);
        }

        public bool Ok => handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE;

        public void Set(bool keepSystem, bool keepDisplay)
        {
            if (!Ok) return;
            if (keepSystem != system)
            {
                if (keepSystem) PowerSetRequest(handle, PowerRequestSystemRequired);
                else PowerClearRequest(handle, PowerRequestSystemRequired);
                system = keepSystem;
            }
            if (keepDisplay != display)
            {
                if (keepDisplay) PowerSetRequest(handle, PowerRequestDisplayRequired);
                else PowerClearRequest(handle, PowerRequestDisplayRequired);
                display = keepDisplay;
            }
        }

        public void Dispose()
        {
            Set(false, false);
            if (Ok) CloseHandle(handle);
        }
    }

    // Power requests do not cover closing the lid; that is a power-plan setting. While agents
    // run (and only if the user turned this on) the lid action is set to "Do nothing", and the
    // original values are written back afterwards. The saved values live in settings.ini so a
    // crash or power loss is repaired on the next launch.
    static class LidAction
    {
        static Guid SubButtons = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        static Guid LidClose = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");

        // Returns the saved "scheme|ac|dc" string, or null with an error message.
        public static string Override(out string error)
        {
            error = null;
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0) { error = "Could not read the active power plan."; return null; }
            Guid scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
            LocalFree(p);

            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidClose, out uint ac) != 0 ||
                PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidClose, out uint dc) != 0)
            { error = "Could not read the lid setting."; return null; }

            if (!Write(scheme, 0, 0))
            {
                // The AC half may have been written before the DC half failed; put it back so the
                // plan is not left changed with nothing saved to restore it from.
                Write(scheme, ac, dc);
                error = "Windows refused the lid setting change.";
                return null;
            }
            return scheme.ToString() + "|" + ac.ToString(CultureInfo.InvariantCulture) + "|" + dc.ToString(CultureInfo.InvariantCulture);
        }

        public static bool Restore(string saved)
        {
            if (!Parse(saved, out Guid scheme, out uint ac, out uint dc)) return true;
            return Write(scheme, ac, dc);
        }

        // The user's own lid action from before Override, for the given power source: 0 do
        // nothing, 1 sleep, 2 hibernate, 3 shut down. Null when the string is not one Override made.
        public static uint? SavedAction(string saved, bool onBattery) =>
            Parse(saved, out _, out uint ac, out uint dc) ? (onBattery ? dc : ac) : (uint?)null;

        static bool Parse(string saved, out Guid scheme, out uint ac, out uint dc)
        {
            var parts = (saved ?? "").Split('|');
            ac = dc = 0;
            scheme = Guid.Empty;
            return parts.Length == 3 && Guid.TryParse(parts[0], out scheme)
                && uint.TryParse(parts[1], out ac) && uint.TryParse(parts[2], out dc);
        }

        // SYSTEM_POWER_CAPABILITIES starts with three BOOLEANs: power button, sleep button, lid.
        // The buffer is bigger than the struct (76 bytes today), so its exact size does not matter.
        [DllImport("powrprof.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool GetPwrCapabilities([Out] byte[] capabilities);

        public static bool HasLid()
        {
            var caps = new byte[256];
            return GetPwrCapabilities(caps) && caps[2] != 0;
        }

        static bool Write(Guid scheme, uint ac, uint dc)
        {
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidClose, ac) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidClose, dc) != 0) return false;
            // Re-applying the plan is what makes the new index take effect.
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) == 0)
            {
                Guid active = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                LocalFree(p);
                if (active == scheme) PowerSetActiveScheme(IntPtr.Zero, ref active);
            }
            return true;
        }
    }

    static class Battery
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // ACLineStatus is 0 on battery, 1 plugged in, 255 unknown; unknown counts as plugged in.
        public static bool OnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

        // The native function takes and returns BOOLEAN, which is one byte, not a 4-byte BOOL.
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
            [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeEventsDisabled);

        // Sleeps or hibernates now, as closing the lid would have, with wake timers left working.
        // False when Windows refused.
        public static bool Suspend(bool hibernate)
        {
            EnableShutdownPrivilege();
            return SetSuspendState(hibernate, false, false);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returnLength);

        // SetSuspendState needs SeShutdownPrivilege enabled, not just held. Standard users hold it
        // on Windows client editions, but it starts out disabled in every process token. If this
        // fails, SetSuspendState fails too and says so.
        static void EnableShutdownPrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return;
            try
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Attributes = SE_PRIVILEGE_ENABLED };
                if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out tp.Luid))
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
    }

    // Reports the lid opening and closing, so the app can carry out the user's own lid action
    // itself while the plan's action is set to "Do nothing". A message-only window is enough:
    // power-setting notifications go to the window that registered, they are not broadcast.
    sealed class LidWatcher : NativeWindow, IDisposable
    {
        static Guid LidSwitchStateChange = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        const int WM_POWERBROADCAST = 0x218, PBT_POWERSETTINGCHANGE = 0x8013, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        // POWERBROADCAST_SETTING is { GUID PowerSetting; DWORD DataLength; UCHAR Data[]; }.
        const int DataLengthOffset = 16, DataOffset = 20;

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
        [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr registration);

        IntPtr registration;

        // Null until Windows reports, which it does with the current state right after
        // registering, and then on every change.
        public bool? Closed { get; private set; }

        // True when the lid closed, false when it opened. Raised on this window's thread, once per
        // change: a report that repeats the current state is not passed on. The first report
        // counts as a change from "not known".
        public event Action<bool> Changed;

        public LidWatcher()
        {
            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
            registration = RegisterPowerSettingNotification(Handle, ref LidSwitchStateChange, DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt64() == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
                if (setting == LidSwitchStateChange && Marshal.ReadInt32(m.LParam, DataLengthOffset) >= 1)
                {
                    // Data is a DWORD: 0 closed, 1 open. Its low byte is enough.
                    bool closed = Marshal.ReadByte(m.LParam, DataOffset) == 0;
                    bool changed = Closed != closed;
                    Closed = closed;
                    if (changed) Changed?.Invoke(closed);
                }
                m.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (registration != IntPtr.Zero) { UnregisterPowerSettingNotification(registration); registration = IntPtr.Zero; }
            DestroyHandle();
        }
    }
}
