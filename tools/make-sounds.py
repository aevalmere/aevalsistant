"""Synthesizes the two notification chimes in assets/sounds/.

    python -I tools/make-sounds.py

Standard library only, and deterministic: there is no randomness, so running it again writes the
same bytes. The sounds are our own, so they carry the repository's MIT license and nothing else.

done.wav       an agent finished: E6 then B5, falling, like a soft "ding-dong"
needs-you.wav  an agent needs you: B5 then E6, rising, like a question

The timbre is a soft mallet on a glass bar: a sine fundamental with a few weaker partials that
die away faster than it does, so each note starts with a little shine and settles into a pure tone.
"""

import array
import math
import os
import sys
import wave

RATE = 22050        # mono, 16-bit; every partial is far below 11025 Hz, and the exe stays small
LENGTH = 0.70       # seconds in the file, tail included
GAP = 0.12          # the second note starts this long after the first
ATTACK = 0.004      # raised-cosine ramp: long enough that the strike does not click
FADE = 0.15         # raised-cosine fade over the end of the file, so it stops on exact zero
PEAK_DBFS = -12.0   # pleasant at normal volume, with room left for the system mixer

# (multiple of the note's frequency, amplitude, decay time constant in seconds). Each partial
# decays faster than the one below it, which is what makes the note sound struck and soft rather
# than buzzy. The last one is slightly inharmonic, as a struck bar's upper modes are, and lasts
# only a few milliseconds.
PARTIALS = [
    (1.0, 1.000, 0.170),
    (2.0, 0.200, 0.075),
    (3.0, 0.060, 0.040),
    (4.2, 0.025, 0.015),
]


def pitch(semitones_from_a4):
    return 440.0 * 2.0 ** (semitones_from_a4 / 12.0)


B5 = pitch(14)   # 987.77 Hz
E6 = pitch(19)   # 1318.51 Hz

SOUNDS = {
    # The answer: falling, the second note a little quieter, so it sounds settled.
    "done.wav": [(0.0, E6, 1.0), (GAP, B5, 0.8)],
    # The question: rising. The second note is barely softer; the first one still ringing under
    # it carries the lift.
    "needs-you.wav": [(0.0, B5, 1.0), (GAP, E6, 0.9)],
}


def render(notes):
    count = round(LENGTH * RATE)
    out = [0.0] * count
    for start, freq, level in notes:
        first = round(start * RATE)
        for ratio, amp, tau in PARTIALS:
            f = freq * ratio
            # A sine sampled below Nyquist cannot alias; keep a wide margin anyway.
            assert f < 0.45 * RATE, f
            step = 2.0 * math.pi * f / RATE
            for i in range(count - first):
                t = i / RATE
                env = math.exp(-t / tau)
                if t < ATTACK:
                    env *= 0.5 - 0.5 * math.cos(math.pi * t / ATTACK)
                # Phase zero at the strike, where the envelope is also zero.
                out[first + i] += level * amp * env * math.sin(step * i)

    fade = round(FADE * RATE)
    for k in range(fade):
        # Ends at cos(pi) = -1, so the last sample is multiplied by exactly zero.
        out[count - fade + k] *= 0.5 + 0.5 * math.cos(math.pi * (k + 1) / fade)

    peak = max(abs(x) for x in out)
    scale = 10.0 ** (PEAK_DBFS / 20.0) * 32767.0 / peak
    pcm = array.array("h", (int(round(x * scale)) for x in out))
    assert pcm[0] == 0 and pcm[-1] == 0
    return pcm


def write(path, pcm):
    data = array.array("h", pcm)
    if sys.byteorder == "big":
        data.byteswap()   # WAV samples are little-endian
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data.tobytes())


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    folder = os.path.join(root, "assets", "sounds")
    os.makedirs(folder, exist_ok=True)
    for name, notes in SOUNDS.items():
        pcm = render(notes)
        path = os.path.join(folder, name)
        write(path, pcm)
        peak = max(abs(s) for s in pcm)
        mean = sum(pcm) / len(pcm)
        print("%-14s %5d samples  %.3f s  peak %.2f dBFS  mean %+.2f  %d bytes" % (
            name, len(pcm), len(pcm) / RATE, 20 * math.log10(peak / 32768.0), mean, os.path.getsize(path)))


if __name__ == "__main__":
    main()
