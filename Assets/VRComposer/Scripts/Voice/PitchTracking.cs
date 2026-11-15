using System;
using System.Collections.Generic;

// Plain C# (no UnityEngine): the singing-to-notes maths, testable outside Unity.
namespace VRComposer.Voice
{
    /// <summary>One analysis frame (10 ms hop).</summary>
    public struct PitchFrame
    {
        public double time;    // seconds, centre of the analysis window
        public double f0;      // Hz, 0 = unvoiced
        public double conf;    // 0..1 periodicity (1 - YIN aperiodicity)
        public double rmsDb;   // loudness, dBFS (32 ms window: voicing gate)
        public double onsetDb; // loudness over 10 ms around the frame centre (sharp: finds the "l" of "la")
    }

    public interface IPitchTracker
    {
        string Name { get; }
        PitchFrame[] Track(float[] samples, int sampleRate);
    }

    /// <summary>
    /// YIN (de Cheveigné & Kawahara 2002): cumulative-mean-normalised difference, absolute threshold,
    /// parabolic interpolation. Tuned for the singing voice (65..1100 Hz) at 16 kHz.
    /// </summary>
    public class YinTracker : IPitchTracker
    {
        public string Name { get { return "YIN"; } }
        public double minHz = 65, maxHz = 1100, threshold = 0.15, hopSeconds = 0.01;

        public PitchFrame[] Track(float[] x, int sr)
        {
            int tauMin = Math.Max(2, (int)(sr / maxHz));
            int tauMax = (int)(sr / minHz) + 1;
            int w = Math.Max(tauMax, (int)(0.032 * sr));    // integration window
            int hop = Math.Max(1, (int)(hopSeconds * sr));
            int need = w + tauMax + 2;
            int count = x.Length >= need ? (x.Length - need) / hop + 1 : 0;
            var frames = new PitchFrame[count];
            var d = new double[tauMax + 2];

            for (int f = 0; f < count; f++)
            {
                int s = f * hop;
                double energy = 0;
                for (int j = 0; j < w; j++) energy += x[s + j] * x[s + j];
                double rms = Math.Sqrt(energy / w);
                var fr = new PitchFrame { time = (s + w * 0.5) / sr, rmsDb = 20 * Math.Log10(rms + 1e-9) };
                int sw = Math.Max(1, sr / 100), sc = s + w / 2 - sw / 2;
                double se = 0;
                for (int j = 0; j < sw; j++) se += x[sc + j] * x[sc + j];
                fr.onsetDb = 20 * Math.Log10(Math.Sqrt(se / sw) + 1e-9);

                // difference function
                for (int tau = 1; tau <= tauMax + 1; tau++)
                {
                    double sum = 0;
                    for (int j = 0; j < w; j++) { double diff = x[s + j] - x[s + j + tau]; sum += diff * diff; }
                    d[tau] = sum;
                }
                // cumulative mean normalised difference
                d[0] = 1;
                double running = 0;
                for (int tau = 1; tau <= tauMax + 1; tau++)
                {
                    running += d[tau];
                    d[tau] = running > 0 ? d[tau] * tau / running : 1;
                }
                // first dip under the threshold (then walk to its bottom); else the global minimum
                int best = -1;
                for (int tau = tauMin; tau <= tauMax; tau++)
                {
                    if (d[tau] < threshold)
                    {
                        while (tau + 1 <= tauMax && d[tau + 1] < d[tau]) tau++;
                        best = tau;
                        break;
                    }
                }
                if (best < 0)
                {
                    best = tauMin;
                    for (int tau = tauMin; tau <= tauMax; tau++) if (d[tau] < d[best]) best = tau;
                }
                double ap = d[best];
                // parabolic interpolation
                double t = best;
                if (best > 1 && best < tauMax + 1)
                {
                    double a = d[best - 1], b = d[best], c = d[best + 1];
                    double den = a - 2 * b + c;
                    if (Math.Abs(den) > 1e-12) t = best + 0.5 * (a - c) / den;
                }
                fr.conf = Math.Max(0, Math.Min(1, 1 - ap));
                fr.f0 = ap < 0.35 && t > 0 ? sr / t : 0;
                frames[f] = fr;
            }
            return frames;
        }
    }

    /// <summary>A sung note in seconds, with its pitch in fractional MIDI (before the tuning fix).</summary>
    public class SungNote
    {
        public double start, end;     // seconds
        public double pitch;          // fractional MIDI (median of the stable middle)
        public int midi;              // rounded after the tuning correction
        public bool afterDip;         // starts at a consonant (energy dip): never merged with the note before
        public override string ToString() { return string.Format("{0:0.000}-{1:0.000} midi {2} ({3:0.00})", start, end, midi, pitch); }
    }

    public class SegmenterSettings
    {
        public double minConf = 0.6;            // periodicity needed to count as voiced
        public double gateBelowPeakDb = 32;     // voiced only within this many dB of the loud parts
        public double minNoteSec = 0.07;
        public double dipDb = 4.5;              // an energy dip this deep (vs both sides) splits a note: "la-la"
        public double dipWindowSec = 0.12;      // look this far each side of a dip for the peaks
        public double pitchJumpSemis = 0.7;     // a held move this big starts a new note
        public double pitchHoldSec = 0.05;
        public double maxGapSec = 0.03;         // unvoiced holes this short are bridged
    }

    /// <summary>
    /// Frames to notes: voicing gate, median-smoothed pitch, splits at energy dips (consonants) and at
    /// held pitch changes, pitch = median of each note's middle half, global tuning offset removed.
    /// </summary>
    public static class NoteSegmenter
    {
        public static double HzToMidi(double hz) { return 69 + 12 * Math.Log(hz / 440.0, 2); }

        public static List<SungNote> Segment(PitchFrame[] fr, SegmenterSettings st, out double tuningCents)
        {
            tuningCents = 0;
            var notes = new List<SungNote>();
            int n = fr.Length;
            if (n < 3) return notes;
            double hop = fr[1].time - fr[0].time;

            // loudness gate relative to the take (works for quiet and loud singers)
            var loud = new List<double>();
            foreach (var f in fr) loud.Add(f.rmsDb);
            loud.Sort();
            double peak = loud[(int)(loud.Count * 0.97)];
            double floor = loud[(int)(loud.Count * 0.10)];
            // (a take with no silence has its "floor" at singing level: never gate closer than 15 dB to the peak)
            double gate = Math.Max(peak - st.gateBelowPeakDb, Math.Min(floor + 8, peak - 15));

            var voiced = new bool[n];
            var pitch = new double[n];
            for (int i = 0; i < n; i++)
            {
                voiced[i] = fr[i].f0 > 0 && fr[i].conf >= st.minConf && fr[i].rmsDb >= gate;
                pitch[i] = voiced[i] ? HzToMidi(fr[i].f0) : double.NaN;
            }
            // octave-error repair: a frame an octave away from both neighbours' median is folded back
            pitch = MedianFilter(pitch, voiced, 5);
            // bridge tiny holes
            int maxGap = (int)Math.Round(st.maxGapSec / hop);
            for (int i = 0; i < n; i++)
            {
                if (voiced[i]) continue;
                int j = i;
                while (j < n && !voiced[j]) j++;
                if (i > 0 && j < n && j - i <= maxGap && Math.Abs(pitch[i - 1] - pitch[j]) < 1.0)
                    for (int k = i; k < j; k++) { voiced[k] = true; pitch[k] = (pitch[i - 1] + pitch[j]) / 2; }
                i = j;
            }

            // sharp loudness for dip detection (the consonant of each "la")
            var db = new double[n];
            for (int i = 0; i < n; i++) db[i] = fr[i].onsetDb;

            // voiced regions
            int r = 0;
            while (r < n)
            {
                if (!voiced[r]) { r++; continue; }
                int e = r;
                while (e < n && voiced[e]) e++;
                SplitRegion(fr, pitch, db, r, e, hop, st, notes);
                r = e;
            }

            // drop blips
            notes.RemoveAll(x => x.end - x.start < st.minNoteSec);

            // global tuning: circular mean of each note's offset from the nearest semitone (duration weighted)
            double sx = 0, sy = 0;
            foreach (var x in notes)
            {
                double frac = x.pitch - Math.Round(x.pitch);
                double w = x.end - x.start;
                sx += w * Math.Cos(2 * Math.PI * frac);
                sy += w * Math.Sin(2 * Math.PI * frac);
            }
            double offset = notes.Count > 0 ? Math.Atan2(sy, sx) / (2 * Math.PI) : 0;
            tuningCents = offset * 100;
            foreach (var x in notes) x.midi = (int)Math.Round(x.pitch - offset);

            // neighbours that ended up on the same note without a consonant between them are one note
            for (int i = notes.Count - 1; i > 0; i--)
            {
                var a = notes[i - 1]; var b = notes[i];
                if (a.midi == b.midi && b.start - a.end < hop * 1.5 && !b.afterDip)
                {
                    a.end = b.end;
                    notes.RemoveAt(i);
                }
            }
            return notes;
        }

        private static void SplitRegion(PitchFrame[] fr, double[] pitch, double[] db, int r0, int r1, double hop,
            SegmenterSettings st, List<SungNote> notes)
        {
            var cuts = new SortedSet<int>();
            var dipCuts = new HashSet<int>();

            // 1. energy dips (the "l" of "la"): a local minimum clearly below the peaks on both sides
            int win = Math.Max(2, (int)Math.Round(st.dipWindowSec / hop));
            int minSep = Math.Max(2, (int)Math.Round(st.minNoteSec / hop));
            int lastCut = r0;
            for (int i = r0 + 1; i < r1 - 1; i++)
            {
                if (!(db[i] <= db[i - 1] && db[i] <= db[i + 1])) continue;
                double lMax = double.MinValue, rMax = double.MinValue;
                for (int k = Math.Max(r0, i - win); k < i; k++) lMax = Math.Max(lMax, db[k]);
                for (int k = i + 1; k <= Math.Min(r1 - 1, i + win); k++) rMax = Math.Max(rMax, db[k]);
                if (lMax - db[i] >= st.dipDb && rMax - db[i] >= st.dipDb && i - lastCut >= minSep && r1 - i >= minSep)
                {
                    cuts.Add(i); dipCuts.Add(i); lastCut = i;
                }
            }

            // 2. held pitch changes (legato steps without a consonant)
            int hold = Math.Max(2, (int)Math.Round(st.pitchHoldSec / hop));
            int segStart = r0;
            for (int i = r0; i < r1; i++)
            {
                if (cuts.Contains(i)) { segStart = i; continue; }
                if (i - segStart < hold) continue;
                double refP = Median(pitch, segStart, i);
                bool moved = true;
                if (i + hold > r1) moved = false;
                else
                    for (int k = i; k < i + hold; k++)
                        if (Math.Abs(pitch[k] - refP) < st.pitchJumpSemis) { moved = false; break; }
                if (moved && i - segStart >= minSep)
                {
                    // put the cut where the pitch crosses half way (the middle of a glide)
                    double target = Median(pitch, i, Math.Min(r1, i + hold * 2));
                    int c = i;
                    for (int k = i; k > segStart + 1; k--)
                        if (Math.Abs(pitch[k] - refP) < Math.Abs(pitch[k] - target)) { c = k + 1; break; }
                    cuts.Add(c);
                    segStart = c;
                    i = c;
                }
            }

            var bounds = new List<int> { r0 };
            foreach (var c in cuts) if (c > r0 && c < r1) bounds.Add(c);
            bounds.Add(r1);
            for (int b = 0; b + 1 < bounds.Count; b++)
            {
                int a = bounds[b], z = bounds[b + 1];
                if (z - a < 2) continue;
                // stable middle: skip the first and last quarter (scoops, falls)
                int q = (z - a) / 4;
                double p = Median(pitch, a + q, z - q);
                if (double.IsNaN(p)) continue;
                notes.Add(new SungNote { start = fr[a].time - hop * 0.5, end = fr[z - 1].time + hop * 0.5, pitch = p, afterDip = dipCuts.Contains(a) });
            }
        }

        private static double Median(double[] v, int a, int z)
        {
            var l = new List<double>();
            for (int i = Math.Max(0, a); i < Math.Min(v.Length, z); i++) if (!double.IsNaN(v[i])) l.Add(v[i]);
            if (l.Count == 0) return double.NaN;
            l.Sort();
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }

        private static double[] MedianFilter(double[] p, bool[] voiced, int size)
        {
            var o = new double[p.Length];
            int h = size / 2;
            var l = new List<double>();
            for (int i = 0; i < p.Length; i++)
            {
                if (!voiced[i]) { o[i] = double.NaN; continue; }
                l.Clear();
                for (int k = Math.Max(0, i - h); k <= Math.Min(p.Length - 1, i + h); k++) if (voiced[k]) l.Add(p[k]);
                l.Sort();
                double med = l[l.Count / 2];
                // fold octave errors back towards the local median
                double v = p[i];
                while (v - med > 9) v -= 12;
                while (med - v > 9) v += 12;
                o[i] = l.Count >= 3 ? (Math.Abs(v - med) > 0.5 ? med : v) : v;
            }
            return o;
        }
    }

    /// <summary>A quantised note in ticks (16ths) relative to the take's first downbeat.</summary>
    public struct TickNote
    {
        public int tick, length, midi;   // midi < 0 = rest
        public bool tieToNext;
        public override string ToString() { return (midi < 0 ? "rest" : "midi " + midi) + " @" + tick + " len " + length + (tieToNext ? " ~" : ""); }
    }

    public static class RhythmQuantizer
    {
        private static readonly int[] Legal = { 16, 12, 8, 6, 4, 3, 2, 1 };

        /// <summary>
        /// Seconds (0 = first downbeat) to ticks on a grid of 'grid' 16ths. A note runs on to the next
        /// note unless the silence is long enough to be a real rest (over half the note, and at least
        /// two grid steps). Then split into legal lengths, never across a barline (tied pieces).
        /// </summary>
        public static List<TickNote> Quantize(List<SungNote> notes, double bpm, int ticksPerBar, int grid, bool addRests)
        {
            double sec16 = 60.0 / bpm / 4.0;
            var on = new List<int>(); var off = new List<int>(); var pitches = new List<int>();
            foreach (var n in notes)
            {
                int a = (int)Math.Round(n.start / sec16 / grid) * grid;
                int z = (int)Math.Round(n.end / sec16 / grid) * grid;
                if (a < 0) a = 0;
                if (on.Count > 0 && a <= on[on.Count - 1])
                {
                    // two onsets on the same slot: keep the longer note, or push this one a step later
                    a = on[on.Count - 1] + grid;
                }
                if (z <= a) z = a + grid;
                on.Add(a); off.Add(z); pitches.Add(n.midi);
            }
            for (int i = 0; i < on.Count; i++)
            {
                if (i + 1 < on.Count)
                {
                    int next = on[i + 1];
                    int len = off[i] - on[i];
                    int gap = next - off[i];
                    if (off[i] > next || gap < Math.Max(2 * grid, (len + 1) / 2)) off[i] = next;
                }
            }

            var result = new List<TickNote>();
            int cursor = on.Count > 0 ? (on[0] / ticksPerBar) * ticksPerBar : 0;
            for (int i = 0; i < on.Count; i++)
            {
                if (addRests && on[i] > cursor) Emit(result, cursor, on[i] - cursor, -1, ticksPerBar);
                Emit(result, on[i], off[i] - on[i], pitches[i], ticksPerBar);
                cursor = off[i];
            }
            return result;
        }

        /// <summary>Splits a span at barlines and into legal values (largest first); notes are tied.</summary>
        public static void Emit(List<TickNote> o, int tick, int length, int midi, int ticksPerBar)
        {
            int end = tick + length;
            int t = tick;
            while (t < end)
            {
                int barEnd = (t / ticksPerBar + 1) * ticksPerBar;
                int room = Math.Min(end, barEnd) - t;
                int piece = 1;
                foreach (var l in Legal)
                    if (l <= room && FitsBeat(t, l)) { piece = l; break; }
                t += piece;
                o.Add(new TickNote { tick = t - piece, length = piece, midi = midi, tieToNext = midi >= 0 && t < end });
            }
        }

        // Keep notes readable: long values start on matching beats (a half on beat 1 or 3, etc.).
        private static bool FitsBeat(int tick, int len)
        {
            if (len >= 8) return tick % 4 == 0;
            if (len >= 4) return tick % 2 == 0;
            return true;
        }
    }
}
