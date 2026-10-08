using System;
using System.Globalization;
using System.Runtime.InteropServices;
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
            var parts = (saved ?? "").Split('|');
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out Guid scheme)
                || !uint.TryParse(parts[1], out uint ac) || !uint.TryParse(parts[2], out uint dc)) return true;
            return Write(scheme, ac, dc);
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
}
