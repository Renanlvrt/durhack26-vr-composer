using System.Collections.Generic;
using UnityEngine;

namespace VRComposer
{
    /// <summary>One-bar melodic idea: a rhythm plus pentatonic steps relative to the first note.</summary>
    public class Motif
    {
        public string name;
        public string hint;
        public Duration[] rhythm;
        public bool[] dotted;          // optional, same length as rhythm
        public int[] degrees;          // pentatonic steps from the first note; int.MinValue = rest
        public int Length16
        {
            get
            {
                int t = 0;
                for (int i = 0; i < rhythm.Length; i++) t += Len(i);
                return t;
            }
        }
        public int Len(int i) { int l = (int)rhythm[i]; return dotted != null && dotted[i] ? l * 3 / 2 : l; }
        public bool IsRest(int i) { return degrees[i] == int.MinValue; }
        public int LastDegree
        {
            get { for (int i = degrees.Length - 1; i >= 0; i--) if (!IsRest(i)) return degrees[i]; return 0; }
        }
    }

    /// <summary>
    /// Beginner mode: pentatonic scale lock, ready-made motifs and chords, and rule-based
    /// "fits next" suggestions. Everything here is deterministic: no randomness, no AI.
    /// </summary>
    public static class BeginnerKit
    {
        private const int R = int.MinValue;
        private static readonly Duration W = Duration.Whole, H = Duration.Half, Q = Duration.Quarter, E = Duration.Eighth;

        public static readonly Motif[] Motifs =
        {
            new Motif { name = "Rising steps", hint = "Walks up", rhythm = new[] { Q, Q, Q, Q }, degrees = new[] { 0, 1, 2, 3 } },
            new Motif { name = "Falling steps", hint = "Walks down", rhythm = new[] { Q, Q, Q, Q }, degrees = new[] { 0, -1, -2, -3 } },
            new Motif { name = "Leap & fall", hint = "Jump, then step back", rhythm = new[] { Q, Q, Q, Q }, degrees = new[] { 0, 3, 2, 1 } },
            new Motif { name = "Echo", hint = "Same idea twice", rhythm = new[] { E, E, Q, E, E, Q }, degrees = new[] { 0, 1, 0, 0, 1, 0 } },
            new Motif { name = "Skip up", hint = "Big friendly jumps", rhythm = new[] { Q, Q, H }, degrees = new[] { 0, 2, 4 } },
            new Motif { name = "Call", hint = "Asks a question (ends high)", rhythm = new[] { E, E, Q, H }, degrees = new[] { 0, 1, 2, 3 } },
            new Motif { name = "Answer", hint = "Replies (ends low)", rhythm = new[] { E, E, Q, H }, degrees = new[] { 0, -1, -2, -3 } },
            new Motif { name = "Long note", hint = "Hold and breathe", rhythm = new[] { W }, degrees = new[] { 0 } },
            new Motif { name = "Ending", hint = "Comes home", rhythm = new[] { Q, Q, H }, degrees = new[] { 2, 1, 0 } },
            new Motif { name = "Bounce", hint = "Playful", rhythm = new[] { E, E, E, E, Q, Q }, degrees = new[] { 0, 2, 0, 2, 1, 0 } },
            new Motif { name = "Lilt", hint = "Dotted swing", rhythm = new[] { Q, E, Q, E }, dotted = new[] { true, false, true, false }, degrees = new[] { 0, 1, 2, 1 } },
            new Motif { name = "Rest & go", hint = "Starts after a breath", rhythm = new[] { Q, Q, Q, Q }, degrees = new[] { R, 0, 1, 2 } },
        };

        // Major-scale degree of each chord block offered, in order of usefulness: I IV V vi ii iii.
        public static readonly int[] ChordDegrees = { 0, 3, 4, 5, 1, 2 };
        private static readonly string[] Roman = { "I", "ii", "iii", "IV", "V", "vi", "vii" };
        private static readonly int[] MajorSemis = { 0, 2, 4, 5, 7, 9, 11 };

        // ---------------------------------------------------------------- scale

        /// <summary>Letters (0 = C .. 6 = B) of the key's pentatonic scale: major degrees 1 2 3 5 6.</summary>
        public static int[] PentatonicLetters(KeyDef k)
        {
            int t = k.majorTonicLetter;
            return new[] { t % 7, (t + 1) % 7, (t + 2) % 7, (t + 4) % 7, (t + 5) % 7 };
        }

        public static bool InScale(KeyDef k, Clef clef, int step)
        {
            int letter = Score.Mod(Score.Diatonic(clef, step), 7);
            foreach (var l in PentatonicLetters(k)) if (l == letter) return true;
            return false;
        }

        /// <summary>Pentatonic index (can be negative) of an in-scale staff step.</summary>
        public static int PentIndex(KeyDef k, Clef clef, int step)
        {
            // walk down from the step to count scale notes relative to a fixed reference (diatonic 0)
            int d = Score.Diatonic(clef, step);
            var letters = PentatonicLetters(k);
            int octave = Score.FloorDiv(d, 7), letter = Score.Mod(d, 7);
            System.Array.Sort(letters);
            int idx = 0;
            for (int i = 0; i < letters.Length; i++) if (letters[i] <= letter) idx = i;
            return octave * 5 + idx;
        }

        public static int StepFromPentIndex(KeyDef k, Clef clef, int p)
        {
            var letters = PentatonicLetters(k);
            System.Array.Sort(letters);
            int octave = Score.FloorDiv(p, 5), i = Score.Mod(p, 5);
            return Score.StepFromDiatonic(clef, octave * 7 + letters[i]);
        }

        /// <summary>Nearest in-scale step to any step (ties go up).</summary>
        public static int SnapToScale(KeyDef k, Clef clef, int step)
        {
            for (int off = 0; off < 4; off++)
            {
                if (InScale(k, clef, step + off)) return step + off;
                if (InScale(k, clef, step - off)) return step - off;
            }
            return step;
        }

        // ---------------------------------------------------------------- chords

        public static string ChordName(KeyDef k, int degree)
        {
            int rootLetter = (k.majorTonicLetter + degree) % 7;
            bool minor = degree == 1 || degree == 2 || degree == 5;
            bool dim = degree == 6;
            return Score.LetterName(rootLetter) + RootAccidental(k, degree) + (minor ? "m" : dim ? "dim" : "");
        }

        public static string ChordRoman(KeyDef k, int degree)
        {
            if (!k.minor) return Roman[degree];
            // relative minor: degree 5 is the tonic
            string[] minorRoman = { "III", "iv", "v", "VI", "VII", "i", "ii°" };
            return minorRoman[degree];
        }

        private static string RootAccidental(KeyDef k, int degree)
        {
            // In G major the 7th degree is F#; in F major the 4th is Bb. Roots offered never need one,
            // except vii in G (F#) and IV in F (Bb).
            int pc = Score.Mod(LetterPc(k.majorTonicLetter) + MajorSemis[degree], 12);
            int natural = LetterPc((k.majorTonicLetter + degree) % 7);
            int diff = Score.Mod(pc - natural + 6, 12) - 6;
            return diff > 0 ? "#" : diff < 0 ? "b" : "";
        }

        private static int LetterPc(int letter) { return MajorSemis[Score.Mod(letter, 7)]; }

        /// <summary>MIDI notes of a chord (close voicing around C4) and its root for the bass.</summary>
        public static int[] ChordMidi(KeyDef k, int degree, out int bassMidi)
        {
            int tonicPc = LetterPc(k.majorTonicLetter);
            int root = 48 + Score.Mod(tonicPc + MajorSemis[degree], 12);    // C3..B3
            int third = 48 + Score.Mod(tonicPc + MajorSemis[(degree + 2) % 7], 12);
            int fifth = 48 + Score.Mod(tonicPc + MajorSemis[(degree + 4) % 7], 12);
            if (third < root) third += 12;
            if (fifth < third) fifth += 12;
            bassMidi = root - 12;
            if (bassMidi < 36) bassMidi += 12;
            return new[] { root + 12, third + 12, fifth + 12 };
        }

        /// <summary>Letters (0..6) of the chord's root, third and fifth.</summary>
        public static int[] ChordLetters(KeyDef k, int degree)
        {
            int r = (k.majorTonicLetter + degree) % 7;
            return new[] { r, (r + 2) % 7, (r + 4) % 7 };
        }

        private static readonly Dictionary<int, int[]> ChordMoves = new Dictionary<int, int[]>
        {
            { 0, new[] { 3, 4, 5 } },    // I  -> IV V vi
            { 1, new[] { 4, 3 } },       // ii -> V IV
            { 2, new[] { 5, 3 } },       // iii -> vi IV
            { 3, new[] { 4, 0, 1 } },    // IV -> V I ii
            { 4, new[] { 0, 5 } },       // V  -> I vi
            { 5, new[] { 3, 1, 4 } },    // vi -> IV ii V
        };

        // ---------------------------------------------------------------- motifs -> notes

        /// <summary>Turns a motif into notes starting at `tick`, its first sounding note on `anchorStep`.</summary>
        public static List<Note> Realise(Motif m, KeyDef k, Clef clef, int tick, int anchorStep, int maxLength16)
        {
            var list = new List<Note>();
            int p0 = PentIndex(k, clef, SnapToScale(k, clef, anchorStep));
            int t = tick;
            for (int i = 0; i < m.rhythm.Length; i++)
            {
                int len = m.Len(i);
                if (t + len > tick + maxLength16) break;
                var n = new Note { tick = t, duration = m.rhythm[i], dotted = m.dotted != null && m.dotted[i], isRest = m.IsRest(i) };
                n.step = n.isRest ? 4 : Mathf.Clamp(StepFromPentIndex(k, clef, p0 + m.degrees[i]), Score.MinStep, Score.MaxStep);
                list.Add(n);
                t += len;
            }
            return list;
        }

        // ---------------------------------------------------------------- suggestions

        public struct MotifSuggestion
        {
            public int motif;
            public int bar;
            public int anchorStep;
            public float score;
        }

        /// <summary>First bar on the melody staff that has no notes yet, after the last used bar.</summary>
        public static int NextEmptyBar(Score s, Staff melody)
        {
            int lastEnd = 0;
            foreach (var n in melody.notes) lastEnd = Mathf.Max(lastEnd, n.End);
            int bar = Mathf.CeilToInt(lastEnd / (float)s.TicksPerBar);
            return bar < s.bars ? bar : -1;
        }

        /// <summary>
        /// Rates every motif for the next empty bar: starts close to the previous note, ends on a
        /// note of that bar's chord, ends on the home note in the last bar, avoids repeating the
        /// previous bar's idea and stays inside the stave. Returns the best few, best first.
        /// </summary>
        public static List<MotifSuggestion> SuggestMotifs(Score s, Staff melody, int count = 3)
        {
            var result = new List<MotifSuggestion>();
            int bar = NextEmptyBar(s, melody);
            if (bar < 0) return result;
            var k = s.Key;
            var clef = melody.clef;
            int barTick = bar * s.TicksPerBar;
            bool lastBar = bar == s.bars - 1;

            // previous sounding note
            Note prev = null;
            foreach (var n in melody.notes) if (!n.isRest && n.tick < barTick && (prev == null || n.tick >= prev.tick)) prev = n;
            int prevP = prev != null ? PentIndex(k, clef, SnapToScale(k, clef, prev.step)) : PentIndex(k, clef, SnapToScale(k, clef, 4));
            int tonicP = PentIndex(k, clef, TonicStepNear(k, clef, 4));

            var chord = s.ChordAt(barTick);
            int[] chordLetters = chord != null ? ChordLetters(k, chord.degree) : null;
            int prevMotif = -1; // unknown: we don't track motif identity on notes

            for (int mi = 0; mi < Motifs.Length; mi++)
            {
                var m = Motifs[mi];
                if (m.Length16 > s.TicksPerBar) continue;
                float best = float.MinValue; int bestAnchor = 0;
                for (int d = -2; d <= 2; d++)
                {
                    int startP = prevP + d;
                    int endP = startP + m.LastDegree;
                    int minP = startP, maxP = startP;
                    foreach (var deg in m.degrees) if (deg != int.MinValue) { minP = Mathf.Min(minP, startP + deg); maxP = Mathf.Max(maxP, startP + deg); }
                    int lowStep = StepFromPentIndex(k, clef, minP), highStep = StepFromPentIndex(k, clef, maxP);

                    float sc = 0f;
                    sc -= Mathf.Abs(d) * 0.6f;                              // smooth connection
                    if (d == 0 && prev != null) sc -= 0.3f;                 // a little movement is nicer than repeating
                    if (lowStep < -2 || highStep > 10) sc -= 3f;            // keep it inside the stave
                    int endLetter = Score.Mod(Score.Diatonic(clef, StepFromPentIndex(k, clef, endP)), 7);
                    if (chordLetters != null && System.Array.IndexOf(chordLetters, endLetter) >= 0) sc += 1.5f;
                    if (lastBar) sc += endP == tonicP || Score.Mod(endP - tonicP, 5) == 0 ? 3f : -1f;
                    if (lastBar && m.name == "Ending") sc += 1f;
                    if (!lastBar && m.name == "Ending") sc -= 1.5f;
                    if (bar == 0 && m.name == "Rest & go") sc -= 0.5f;
                    if (sc > best) { best = sc; bestAnchor = StepFromPentIndex(k, clef, startP); }
                }
                if (mi == prevMotif) best -= 1f;
                result.Add(new MotifSuggestion { motif = mi, bar = bar, anchorStep = bestAnchor, score = best - mi * 0.01f });
            }
            result.Sort((a, b) => b.score.CompareTo(a.score));
            if (result.Count > count) result.RemoveRange(count, result.Count - count);
            return result;
        }

        /// <summary>Best anchor when a motif tile is tapped (placed in the next empty bar).</summary>
        public static bool BestPlacement(Score s, Staff melody, int motif, out int bar, out int anchor)
        {
            bar = NextEmptyBar(s, melody);
            anchor = 4;
            if (bar < 0) return false;
            foreach (var sg in SuggestMotifs(s, melody, Motifs.Length))
                if (sg.motif == motif) { anchor = sg.anchorStep; return true; }
            anchor = SnapToScale(s.Key, melody.clef, 4);
            return true;
        }

        public static int TonicStepNear(KeyDef k, Clef clef, int step)
        {
            int best = step, bestDist = 99;
            for (int st = step - 7; st <= step + 7; st++)
            {
                if (Score.Mod(Score.Diatonic(clef, st), 7) != k.tonicLetter) continue;
                int dist = Mathf.Abs(st - step);
                if (dist < bestDist) { bestDist = dist; best = st; }
            }
            return best;
        }

        /// <summary>First bar without a chord.</summary>
        public static int NextEmptyChordBar(Score s)
        {
            for (int b = 0; b < s.bars; b++) if (s.ChordAt(b * s.TicksPerBar) == null) return b;
            return -1;
        }

        /// <summary>Chord degrees that fit the next empty chord bar: common moves from the previous
        /// chord, plus how many melody notes in that bar belong to the chord.</summary>
        public static List<int> SuggestChords(Score s, Staff melody, int count = 3)
        {
            var list = new List<int>();
            int bar = NextEmptyChordBar(s);
            if (bar < 0) return list;
            var k = s.Key;
            int home = k.minor ? 5 : 0;
            int barTick = bar * s.TicksPerBar;
            var prev = bar > 0 ? s.ChordAt(barTick - 1) : null;

            var scores = new Dictionary<int, float>();
            foreach (var deg in ChordDegrees)
            {
                float sc = 0f;
                if (prev == null) sc += deg == home ? 3f : 0f;
                else
                {
                    int[] moves;
                    if (ChordMoves.TryGetValue(prev.degree, out moves))
                    {
                        int idx = System.Array.IndexOf(moves, deg);
                        if (idx >= 0) sc += 2f - idx * 0.4f;
                    }
                    if (deg == prev.degree) sc -= 1f;
                }
                if (bar == s.bars - 1) sc += deg == home ? 2.5f : 0f;
                if (bar == s.bars - 2) sc += deg == 4 ? 1f : 0f;          // V before the final bar

                if (melody != null)
                {
                    var letters = ChordLetters(k, deg);
                    foreach (var n in melody.notes)
                    {
                        if (n.isRest || n.tick < barTick || n.tick >= barTick + s.TicksPerBar) continue;
                        int letter = Score.Mod(Score.Diatonic(melody.clef, n.step), 7);
                        if (System.Array.IndexOf(letters, letter) >= 0) sc += 0.5f * n.Length16 / 4f;
                    }
                }
                scores[deg] = sc;
            }
            var ordered = new List<int>(ChordDegrees);
            ordered.Sort((a, b) => scores[b].CompareTo(scores[a]));
            for (int i = 0; i < count && i < ordered.Count; i++) list.Add(ordered[i]);
            return list;
        }
    }
}
