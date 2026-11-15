using System;
using System.Collections.Generic;

namespace VRComposer
{
    /// <summary>One sounding note after ties, articulations and dynamics are applied (score time).</summary>
    public struct PerfNote
    {
        public int staff;
        public float startTick, endTick;   // 16th-note ticks, before the fermata time map
        public int midi;
        public int velocity;
    }

    /// <summary>
    /// Score time (ticks) to performance time (quarter-note beats), with fermatas held longer.
    /// A fermata adds (Hold - 1) x the note's length, spread over its last quarter beat, so the
    /// note is attacked in tempo and the pause comes at its end. Where staves differ, the longest
    /// hold wins, so the whole band waits together.
    /// </summary>
    public class TimeMap
    {
        public const float Hold = 2f;
        private readonly float[] beatAt;    // beatAt[t] = beats elapsed at the start of tick t

        public int TotalTicks { get { return beatAt.Length - 1; } }
        public float TotalBeats { get { return beatAt[beatAt.Length - 1]; } }

        public TimeMap(Score score)
        {
            int total = Math.Max(1, score.TotalTicks);
            var factor = new float[total];
            for (int t = 0; t < total; t++) factor[t] = 1f;
            foreach (var st in score.staves)
                foreach (var n in st.notes)
                {
                    if (!n.fermata || n.tick >= total) continue;
                    int end = Math.Min(n.End, total);
                    int len = end - n.tick;
                    if (len <= 0) continue;
                    int span = Math.Min(len, 4);
                    float f = 1f + len * (Hold - 1f) / span;
                    for (int t = end - span; t < end; t++) factor[t] = Math.Max(factor[t], f);
                }
            beatAt = new float[total + 1];
            for (int t = 0; t < total; t++) beatAt[t + 1] = beatAt[t] + 0.25f * factor[t];
        }

        public float Beats(float tick)
        {
            if (tick <= 0) return tick * 0.25f;
            int total = TotalTicks;
            if (tick >= total) return beatAt[total] + (tick - total) * 0.25f;
            int i = (int)tick;
            return beatAt[i] + (beatAt[i + 1] - beatAt[i]) * (tick - i);
        }

        public float Ticks(float beats)
        {
            if (beats <= 0) return beats * 4f;
            int total = TotalTicks;
            if (beats >= beatAt[total]) return total + (beats - beatAt[total]) * 4f;
            int lo = 0, hi = total;          // largest i with beatAt[i] <= beats
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (beatAt[mid] <= beats) lo = mid; else hi = mid; }
            float w = beatAt[lo + 1] - beatAt[lo];
            return lo + (w > 0 ? (beats - beatAt[lo]) / w : 0f);
        }
    }

    public static class Performance
    {
        /// <summary>Velocity of each dynamic level (-2 pp ... +2 ff); 0 = unmarked (mezzo).</summary>
        public static int LevelVelocity(int dynamic)
        {
            switch (dynamic)
            {
                case -2: return 40;
                case -1: return 58;
                case 1: return 104;
                case 2: return 120;
                default: return 88;
            }
        }

        /// <summary>
        /// Turns the written notes into sounding notes:
        /// - ties merge into one long note (only onto a following note of the same pitch that starts
        ///   exactly where the tied note ends, so a tie left behind by an edit is simply ignored);
        /// - dynamics last until the next marking on the same staff; cresc./dim. ramps to it;
        /// - sf and accent boost one note; staccato halves it.
        /// </summary>
        public static List<PerfNote> Realize(Score score)
        {
            var result = new List<PerfNote>();
            int totalTicks = score.TotalTicks;
            for (int si = 0; si < score.staves.Count; si++)
            {
                var staff = score.staves[si];
                var notes = new List<Note>(staff.notes);
                // In a chord the marking sits on one note: process that one first so its mates share it.
                notes.Sort((a, b) =>
                {
                    if (a.tick != b.tick) return a.tick.CompareTo(b.tick);
                    int ma = (a.dynamic != 0 || a.hairpin != 0) ? 0 : 1, mb = (b.dynamic != 0 || b.hairpin != 0) ? 0 : 1;
                    return ma != mb ? ma.CompareTo(mb) : a.step.CompareTo(b.step);
                });

                // dynamic level per note (carried forward), with cresc./dim. ramps
                var vel = new Dictionary<Note, int>();
                int level = LevelVelocity(0);
                for (int i = 0; i < notes.Count; i++)
                {
                    var n = notes[i];
                    if (n.dynamic != 0) level = LevelVelocity(n.dynamic);
                    if (n.hairpin != 0)
                    {
                        // ramp from here to the next marking (or +/-30 by the end if there is none)
                        int j = i + 1;
                        while (j < notes.Count && notes[j].dynamic == 0) j++;
                        int target = j < notes.Count ? LevelVelocity(notes[j].dynamic) : level + 30 * n.hairpin;
                        int t0 = n.tick, t1 = j < notes.Count ? notes[j].tick : totalTicks;
                        for (int k = i; k < j; k++)
                        {
                            float a = t1 > t0 ? (notes[k].tick - t0) / (float)(t1 - t0) : 0f;
                            vel[notes[k]] = Clamp((int)Math.Round(level + (target - level) * a), 1, 127);
                        }
                        i = j - 1;
                        continue;
                    }
                    vel[n] = level;
                }

                var consumed = new HashSet<Note>();
                foreach (var n in notes)
                {
                    if (n.isRest || consumed.Contains(n) || n.tick >= totalTicks) continue;
                    int midi = score.MidiOf(staff, n);
                    int end = n.End;
                    var cur = n;
                    while (cur.tie)
                    {
                        Note next = null;
                        foreach (var m in notes)
                            if (!m.isRest && m.tick == cur.End && !consumed.Contains(m) && score.MidiOf(staff, m) == midi) { next = m; break; }
                        if (next == null) break;
                        consumed.Add(next);
                        end = next.End;
                        cur = next;
                    }
                    float start = n.tick;
                    float stop = Math.Min(end, totalTicks);
                    if (cur.staccato) stop = Math.Max(start + 0.5f, stop - (cur.End - cur.tick) * 0.5f);
                    float gap = Math.Min(0.2f, (stop - start) * 0.1f);    // tiny gap so notes re-articulate
                    // The same pitch again straight after (Beethoven's G-G-G): string patches release
                    // slowly, so a tiny gap smears the notes into one tone. Play this one ~70% long.
                    if (!cur.staccato)
                        foreach (var m in notes)
                            if (!m.isRest && m.tick == cur.End && m != cur && score.MidiOf(staff, m) == midi && !consumed.Contains(m))
                            { gap = Math.Max(gap, Math.Min(1.2f, (stop - start) * 0.3f)); break; }

                    int v;
                    if (!vel.TryGetValue(n, out v)) v = LevelVelocity(0);
                    if (n.sforzando) v = Math.Min(127, Math.Max(v, LevelVelocity(1)) + 18);
                    if (n.accent) v = Math.Min(127, v + 20);
                    result.Add(new PerfNote { staff = si, startTick = start, endTick = stop - gap, midi = midi, velocity = Clamp(v, 1, 127) });
                }
            }
            return result;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
