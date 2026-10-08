using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Aevalsistant
{
    static partial class Tests
    {
        const int WavRate = 22050;
        const double NoteB5 = 987.77, NoteE6 = 1318.51;

        // Nothing here plays a sound or puts the PC to sleep.
        static void PlatformChecks()
        {
            ChimeChecks("done.wav", NoteE6, NoteB5);
            ChimeChecks("needs-you.wav", NoteB5, NoteE6);

            bool hasLid = false, lidThrew = false;
            try { hasLid = LidAction.HasLid(); }
            catch (DllNotFoundException) { lidThrew = true; }          // no powrprof.dll
            catch (EntryPointNotFoundException) { lidThrew = true; }   // no GetPwrCapabilities in it
            Check(!lidThrew, "lid: HasLid answers");
            Console.WriteLine("platform: has lid " + hasLid + ", on battery " + Battery.OnBattery());

            string saved = Guid.NewGuid() + "|1|2";
            Check(LidAction.SavedAction(saved, onBattery: false) == 1 && LidAction.SavedAction(saved, onBattery: true) == 2,
                "lid: saved action is read per power source");
            Check(LidAction.SavedAction("junk", false) == null && LidAction.SavedAction(null, true) == null
                && LidAction.SavedAction(Guid.NewGuid() + "|1", false) == null && LidAction.SavedAction("x|1|2", true) == null
                && LidAction.SavedAction(Guid.NewGuid() + "|1|sleep", true) == null, "lid: a saved string that does not parse gives null");

            LidWatcherChecks();
        }

        // The chimes come from tools/make-sounds.py. Nobody can listen to them in a test, so this
        // measures what listening would judge: format, length, level, a click-free start and end,
        // a soft attack, and the right two notes in the right order.
        static void ChimeChecks(string name, double first, double second)
        {
            byte[] file;
            using (var s = typeof(Sound).Assembly.GetManifestResourceStream(name))
            {
                Check(s != null, name + ": embedded in the exe");
                if (s == null) return;
                file = new byte[s.Length];
                s.Read(file, 0, file.Length);
            }
            short[] pcm = ReadWav(file, out string found);
            Check(pcm != null, name + ": RIFF/WAVE, PCM, mono, 16-bit, 22050 Hz (found " + found + ")");
            if (pcm == null || pcm.Length < WavRate / 4) return;

            double seconds = pcm.Length / (double)WavRate;
            Check(seconds < 0.8, name + ": shorter than 0.8 s");

            int peak = 0; long sum = 0;
            foreach (short v in pcm) { peak = Math.Max(peak, Math.Abs((int)v)); sum += v; }
            double peakDb = 20 * Math.Log10(peak / 32768.0);
            Check(peakDb >= -14 && peakDb <= -10, name + ": peak between -14 and -10 dBFS");
            Check(Math.Abs(sum / (double)pcm.Length) < 0.001 * 32768, name + ": no DC offset");
            Check(pcm[0] == 0 && pcm[pcm.Length - 1] == 0, name + ": starts and ends on zero, so it cannot click");

            double rise = WavRms(pcm, 0, 0.003), body = WavRms(pcm, 0.003, 0.010);
            Check(rise < body, name + ": soft attack (first 3 ms quieter than 3 to 10 ms)");

            // The second note starts 120 ms in. The first window has only the first note; in the
            // second, the first note is still ringing but the new one is louder.
            double heard1 = LoudestNote(pcm, 0.010, 0.110), heard2 = LoudestNote(pcm, 0.130, 0.230);
            Check(Math.Abs(heard1 - first) < first * 0.03, name + ": first note is " + first + " Hz");
            Check(Math.Abs(heard2 - second) < second * 0.03, name + ": second note is " + second + " Hz");
            Check(Math.Sign(heard2 - heard1) == Math.Sign(second - first),
                name + (second < first ? ": second note is lower" : ": second note is higher"));

            Console.WriteLine(FormattableString.Invariant(
                $"sound {name}: {file.Length} bytes, {seconds:0.000} s, peak {peakDb:0.00} dBFS, rms 0-3 ms {Db(rise):0.0} dBFS, 3-10 ms {Db(body):0.0} dBFS, notes {heard1:0} Hz then {heard2:0} Hz"));
        }

        static double Db(double rms) => 20 * Math.Log10(Math.Max(rms, 1e-9) / 32768.0);

        // The samples of a mono 16-bit 22050 Hz PCM WAV, or null with what the file holds instead.
        static short[] ReadWav(byte[] b, out string found)
        {
            found = "no RIFF/WAVE header";
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
            int format = 0, channels = 0, rate = 0, bits = 0;
            short[] data = null;
            for (int at = 12; at + 8 <= b.Length;)
            {
                string id = Encoding.ASCII.GetString(b, at, 4);
                int size = BitConverter.ToInt32(b, at + 4), body = at + 8;
                if (size < 0 || body + size > b.Length) { found = "a truncated '" + id + "' chunk"; return null; }
                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    data = new short[size / 2];
                    Buffer.BlockCopy(b, body, data, 0, data.Length * 2);
                }
                at = body + size + (size & 1);   // chunks are padded to an even length
            }
            found = "format " + format + ", " + channels + " channel(s), " + bits + "-bit, " + rate + " Hz" + (data == null ? ", no data" : "");
            return format == 1 && channels == 1 && bits == 16 && rate == WavRate ? data : null;
        }

        static double WavRms(short[] pcm, double from, double to)
        {
            int a = (int)(from * WavRate), z = (int)(to * WavRate);
            double sum = 0;
            for (int i = a; i < z; i++) sum += (double)pcm[i] * pcm[i];
            return Math.Sqrt(sum / (z - a));
        }

        // The loudest semitone from E5 to A6 in a window, by a Hann-windowed DFT at each one. The
        // range stops below the notes' own second partials (1976 Hz and up).
        static double LoudestNote(short[] pcm, double from, double to)
        {
            int a = (int)(from * WavRate), n = (int)(to * WavRate) - a;
            double best = 0, bestPower = -1;
            for (int k = 7; k <= 24; k++)
            {
                double f = 440 * Math.Pow(2, k / 12.0), re = 0, im = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = pcm[a + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
                    double phase = 2 * Math.PI * f * i / WavRate;
                    re += x * Math.Cos(phase);
                    im -= x * Math.Sin(phase);
                }
                if (re * re + im * im > bestPower) { bestPower = re * re + im * im; best = f; }
            }
            return best;
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        static void LidWatcherChecks()
        {
            var lid = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
            var acdc = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");   // GUID_ACDC_POWER_SOURCE
            var w = new LidWatcher();
            Check(w.Handle != IntPtr.Zero, "lid watcher: window created");

            // Windows sends the current state soon after registering.
            var clock = Stopwatch.StartNew();
            while (w.Closed == null && clock.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(10); }
            Console.WriteLine("lid watcher: Windows reported " + (w.Closed == null ? "nothing" : w.Closed == true ? "closed" : "open")
                + " within " + clock.ElapsedMilliseconds + " ms");

            // Then the parsing, with made-up notifications sent straight to the window. Start from
            // the opposite of what Windows said, so the first one is a change.
            var seen = new List<bool>();
            w.Changed += closed => seen.Add(closed);
            bool next = w.Closed != true;
            SendPowerSetting(w.Handle, lid, next);
            SendPowerSetting(w.Handle, lid, next);
            SendPowerSetting(w.Handle, acdc, !next);
            SendPowerSetting(w.Handle, lid, !next);
            Check(seen.Count == 2 && seen[0] == next && seen[1] == !next && w.Closed == !next,
                "lid watcher: reads the lid state and passes on changes only");

            w.Dispose();
            Check(w.Handle == IntPtr.Zero, "lid watcher: dispose destroys the window");
        }

        // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; DWORD Data; } as Windows
        // sends it for the lid, with Data 0 for closed and 1 for open.
        static void SendPowerSetting(IntPtr hwnd, Guid setting, bool closed)
        {
            IntPtr p = Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(setting.ToByteArray(), 0, p, 16);
                Marshal.WriteInt32(p, 16, 4);
                Marshal.WriteInt32(p, 20, closed ? 0 : 1);
                SendMessage(hwnd, 0x218, new IntPtr(0x8013), p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }
}
