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

                // sharp loudness for consonant dips: at least 2.5 pitch periods (10 ms for high voices,
                // up to 30 ms for basses) so single glottal pulses never look like dips
                int sw = sr / 100;
                if (fr.f0 > 0) sw = Math.Max(sw, Math.Min((int)(0.03 * sr), (int)(2.5 * sr / fr.f0)));
                sw = Math.Min(sw, w);
                int sc = s + w / 2 - sw / 2;
                double se = 0;
                for (int j = 0; j < sw; j++) se += x[sc + j] * x[sc + j];
                fr.onsetDb = 20 * Math.Log10(Math.Sqrt(se / sw) + 1e-9);
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
        public double minConf = 0.7;            // periodicity needed to count as voiced (tuned on Vocadito)
        public double gateBelowPeakDb = 32;     // voiced only within this many dB of the loud parts
        public double minNoteSec = 0.07;
        public double dipDb = 6;                // an energy dip this deep (vs both sides) splits a note: "la-la"
        public double dipWindowSec = 0.12;      // look this far each side of a dip for the peaks
        public double pitchJumpSemis = 1.2;     // a held move this big starts a new note (tuned on Vocadito)
        public double pitchHoldSec = 0.08;
        public double maxGapSec = 0.03;         // unvoiced holes this short are bridged
        public double glideSlope = 0.15;        // semitones per 10 ms: faster = a glide/scoop, not a note (crepe_notes)
        public double tailDropDb = 12;          // a phrase ends this far below its own peak (aubio): no quiet octave tails
        public double minTuningAgreement = 0.6; // apply the tuning offset only if the notes agree on it
        public bool keySnap = false;            // notes sung between two semitones take the one in the key (off: worse on Vocadito)
        public double ambiguousFrom = 0.3;      // "between" = fraction 0.3..0.7 of a semitone
        public double riseDb = 4;               // pYIN onset rule: a loudness rise this big over 20 ms starts a syllable (0 = off)
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
            // longer-range octave check (+-150 ms): a stretch an octave off its surroundings is folded back
            {
                var fixedP = (double[])pitch.Clone();
                var w = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    if (!voiced[i]) continue;
                    w.Clear();
                    for (int j = Math.Max(0, i - 15); j <= Math.Min(n - 1, i + 15); j++) if (voiced[j]) w.Add(pitch[j]);
                    w.Sort();
                    double med = w[w.Count / 2], d = pitch[i] - med;
                    if (Math.Abs(d - 12) < 2) fixedP[i] = pitch[i] - 12;
                    else if (Math.Abs(d + 12) < 2) fixedP[i] = pitch[i] + 12;
                }
                pitch = fixedP;
            }
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
                // the phrase ends once it falls tailDropDb below its own peak (quiet tails track octaves wrong)
                int pk = r;
                for (int i = r; i < e; i++) if (fr[i].rmsDb > fr[pk].rmsDb) pk = i;
                int end = e;
                while (end - 1 > pk && fr[end - 1].rmsDb < fr[pk].rmsDb - st.tailDropDb) end--;   // only the final fade
                if (end - r >= 2) SplitRegion(fr, pitch, db, r, end, hop, st, notes);
                r = e;
            }

            // drop blips
            notes.RemoveAll(x => x.end - x.start < st.minNoteSec);

            // global tuning: circular mean of each note's offset from the nearest semitone (duration weighted)
            double sx = 0, sy = 0, wsum = 0;
            foreach (var x in notes)
            {
                double frac = x.pitch - Math.Round(x.pitch);
                double w = x.end - x.start;
                sx += w * Math.Cos(2 * Math.PI * frac);
                sy += w * Math.Sin(2 * Math.PI * frac);
                wsum += w;
            }
            double agreement = wsum > 0 ? Math.Sqrt(sx * sx + sy * sy) / wsum : 0;
            // only trust a tuning offset that the notes agree on (a few glide-y notes give a random one)
            double offset = notes.Count >= 4 && agreement >= st.minTuningAgreement ? Math.Atan2(sy, sx) / (2 * Math.PI) : 0;
            tuningCents = offset * 100;
            foreach (var x in notes) x.midi = (int)Math.Round(x.pitch - offset);
            if (st.keySnap) KeySnap(notes, offset, st.ambiguousFrom);

            // glissando fragments (Ryynanen & Klapuri): a short piece whose pitch lies between its two
            // neighbours, with no consonant around it, is the slide between them: it joins the note it lands on
            for (int i = notes.Count - 2; i >= 1; i--)
            {
                var p = notes[i - 1]; var f = notes[i]; var q = notes[i + 1];
                bool touching = f.start - p.end < 0.03 && q.start - f.end < 0.03;
                double lo = Math.Min(p.pitch, q.pitch), hi = Math.Max(p.pitch, q.pitch);
                bool between = f.pitch > lo + 0.25 && f.pitch < hi - 0.25;
                // (a consonant before the slide is fine: "la" then a scoop up to the note)
                if (touching && between && f.end - f.start < 0.16 && !q.afterDip)
                {
                    q.start = f.start;
                    q.afterDip = f.afterDip;
                    notes.RemoveAt(i);
                }
            }

            // octave blips: a short note an octave away from the note it touches is a tracking error
            // (common at the start of low notes): give its time to that neighbour
            for (int i = 0; i < notes.Count && notes.Count > 1; i++)
            {
                var x = notes[i];
                if (x.end - x.start > 0.16) continue;
                SungNote nb = null;
                if (i + 1 < notes.Count && notes[i + 1].start - x.end < 0.05 && Math.Abs(Math.Abs(notes[i + 1].midi - x.midi) - 12) <= 1) nb = notes[i + 1];
                else if (i > 0 && x.start - notes[i - 1].end < 0.05 && Math.Abs(Math.Abs(notes[i - 1].midi - x.midi) - 12) <= 1) nb = notes[i - 1];
                if (nb == null) continue;
                nb.start = Math.Min(nb.start, x.start);
                nb.end = Math.Max(nb.end, x.end);
                if (nb.start == x.start) nb.afterDip = x.afterDip;
                notes.RemoveAt(i);
                i = -1;
            }

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

            // 1b. sudden loudness rises (pYIN/Tony onset rule)
            if (st.riseDb > 0)
            {
                int g = Math.Max(1, (int)Math.Round(0.02 / hop));
                for (int i = r0 + 1; i + g < r1; i++)
                {
                    if (db[i + g] - db[i] < st.riseDb || db[i] > db[i - 1] + 0.5) continue;   // start of a rise only
                    bool near = false;
                    foreach (var c in cuts) if (Math.Abs(c - i) < minSep) { near = true; break; }
                    if (!near && i - r0 >= minSep && r1 - i >= minSep) { cuts.Add(i); dipCuts.Add(i); }
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

            // glide frames (fast pitch movement or low confidence) never vote on a note's pitch
            double k10 = hop / 0.01;
            var glide = new bool[r1 - r0];
            for (int i = r0; i < r1; i++)
            {
                double prev = i > r0 ? pitch[i - 1] : pitch[i], next = i + 1 < r1 ? pitch[i + 1] : pitch[i];
                double slope = Math.Abs(next - prev) / ((i > r0 && i + 1 < r1) ? 2 : 1) / k10;
                glide[i - r0] = double.IsNaN(slope) || slope > st.glideSlope || fr[i].conf < 0.65;
            }

            var bounds = new List<int> { r0 };
            foreach (var c in cuts) if (c > r0 && c < r1) bounds.Add(c);
            bounds.Add(r1);
            int carry = -1;            // start of a slide-only piece, given to the note it lands on
            bool carryDip = false;
            for (int b = 0; b + 1 < bounds.Count; b++)
            {
                int a = bounds[b], z = bounds[b + 1];
                if (z - a < 2) continue;
                // stable frames: not gliding, and not the outer 15% (scoop in, fall out)
                int q = (z - a) * 15 / 100;
                var l = new List<double>();
                for (int i = a + q; i < z - q; i++) if (!glide[i - r0] && !double.IsNaN(pitch[i])) l.Add(pitch[i]);
                if (l.Count < 3)
                {
                    // a piece that is only a slide: not a note of its own
                    if (carry < 0) { carry = a; carryDip = dipCuts.Contains(a); }
                    continue;
                }
                l.Sort();
                double p = l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
                int s0 = carry >= 0 ? carry : a;
                notes.Add(new SungNote { start = fr[s0].time - hop * 0.5, end = fr[z - 1].time + hop * 0.5, pitch = p, afterDip = carry >= 0 ? carryDip : dipCuts.Contains(a) });
                carry = -1;
            }
            // a trailing slide belongs to the last note
            if (carry >= 0 && notes.Count > 0 && notes[notes.Count - 1].end >= fr[carry].time - hop) notes[notes.Count - 1].end = fr[r1 - 1].time + hop * 0.5;
        }

        // Krumhansl-Kessler key profiles (major, minor), index 0 = tonic
        private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly int[] MinorScale = { 0, 2, 3, 5, 7, 8, 10 };

        /// <summary>
        /// A note sung half-way between two semitones (|fraction| near 0.5 after the tuning fix) takes
        /// the neighbour that belongs to the key of the take (Krumhansl-Kessler key finding on the
        /// duration-weighted notes). Clear notes are never changed.
        /// </summary>
        public static void KeySnap(List<SungNote> notes, double offset, double ambiguousFrom)
        {
            if (notes.Count < 4) return;
            var hist = new double[12];
            foreach (var x in notes) hist[((x.midi % 12) + 12) % 12] += x.end - x.start;
            double best = double.MinValue; int tonic = 0; bool minor = false;
            for (int t = 0; t < 12; t++)
                for (int mode = 0; mode < 2; mode++)
                {
                    var prof = mode == 0 ? MajorProfile : MinorProfile;
                    double c = Correlation(hist, prof, t);
                    if (c > best) { best = c; tonic = t; minor = mode == 1; }
                }
            if (best < 0.5) return;   // no clear key: leave the notes as sung
            var scale = new bool[12];
            foreach (var d in minor ? MinorScale : MajorScale) scale[(tonic + d) % 12] = true;
            foreach (var x in notes)
            {
                double q = x.pitch - offset;
                double lo = Math.Floor(q), frac = q - lo;
                if (frac < ambiguousFrom || frac > 1 - ambiguousFrom) continue;
                bool loIn = scale[(((int)lo % 12) + 12) % 12], hiIn = scale[((((int)lo + 1) % 12) + 12) % 12];
                if (loIn && !hiIn) x.midi = (int)lo;
                else if (hiIn && !loIn) x.midi = (int)lo + 1;
            }
        }

        private static double Correlation(double[] h, double[] prof, int tonic)
        {
            double mh = 0, mp = 0;
            for (int i = 0; i < 12; i++) { mh += h[i]; mp += prof[i]; }
            mh /= 12; mp /= 12;
            double num = 0, dh = 0, dp = 0;
            for (int i = 0; i < 12; i++)
            {
                double a = h[(tonic + i) % 12] - mh, b = prof[i] - mp;
                num += a * b; dh += a * a; dp += b * b;
            }
            return dh > 0 && dp > 0 ? num / Math.Sqrt(dh * dp) : 0;
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
