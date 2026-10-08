using System;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Aevalsistant
{
    // The optional chime when a card appears. Both sounds are synthesized by tools/make-sounds.py
    // and embedded in the exe, so there is nothing to license and no file to go missing.
    static class Sound
    {
        // One player per sound, made on first use and kept for the life of the app, along with
        // its stream and the WAV bytes it read from it. SoundPlayer hands those bytes to
        // PlaySound(SND_MEMORY | SND_ASYNC), which since Windows 7 copies a WAV under 2 MB before
        // returning; if that copy fails it plays from our bytes, so they must outlive the sound.
        static SoundPlayer done, needsYou;

        const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

        public static void Play(ToastKind kind)
        {
            if (kind == ToastKind.Info || DoNotDisturb()) return;
            try
            {
                var player = kind == ToastKind.Done
                    ? done ?? (done = Load("done.wav"))
                    : needsYou ?? (needsYou = Load("needs-you.wav"));
                // Returns at once and plays on a system thread. Windows plays one such sound per
                // process, so a newer chime cuts off one still ringing, which is what we want.
                player?.Play();
            }
            // SoundPlayer checks the header before playing and refuses anything but PCM WAV.
            catch (InvalidOperationException) { }
            // Load waits for a load already in progress and gives up after LoadTimeout.
            catch (TimeoutException) { }
        }

        static SoundPlayer Load(string name)
        {
            // The resource stream is a view of the exe image already in memory, so there is no
            // handle to close; Load copies the bytes out of it once.
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            var player = new SoundPlayer(s);
            player.Load();
            return player;
        }

        // Windows holds its own notification sounds back during a full-screen app, a game, or a
        // presentation; so do we.
        static bool DoNotDisturb() =>
            SHQueryUserNotificationState(out int state) == 0
            && (state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE);
    }
}
