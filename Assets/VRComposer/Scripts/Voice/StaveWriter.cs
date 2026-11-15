using System.Collections.Generic;
using UnityEngine;

namespace VRComposer.Voice
{
    /// <summary>Turns sounding pitches (MIDI) in ticks into stave notes for a staff, key and mode.</summary>
    public static class StaveWriter
    {
        public static bool LengthToDuration(int len, out Duration d, out bool dotted)
        {
            dotted = false;
            switch (len)
            {
                case 16: d = Duration.Whole; return true;
                case 12: d = Duration.Half; dotted = true; return true;
                case 8: d = Duration.Half; return true;
                case 6: d = Duration.Quarter; dotted = true; return true;
                case 4: d = Duration.Quarter; return true;
                case 3: d = Duration.Eighth; dotted = true; return true;
                case 2: d = Duration.Eighth; return true;
                case 1: d = Duration.Sixteenth; return true;
                default: d = Duration.Quarter; return false;
            }
        }

        /// <summary>
        /// Octave shift (in semitones) that puts the most notes inside the stave's range, then keeps the
        /// melody's middle closest to the middle line. A man singing for a treble instrument goes up an
        /// octave; nothing is shifted by less than an octave.
        /// </summary>
        public static int BestOctaveShift(Score score, Clef clef, List<int> midis)
        {
            if (midis.Count == 0) return 0;
            int best = 0, bestOut = int.MaxValue; float bestDist = float.MaxValue;
            for (int k = -3; k <= 3; k++)
            {
                int outCount = 0;
                var steps = new List<int>();
                foreach (var m in midis)
                {
                    int acc; bool nat;
                    int st = Score.StepFromMidi(clef, m + 12 * k, score.keySignature, out acc, out nat);
                    if (st < Score.MinStep || st > Score.MaxStep) outCount++;
                    steps.Add(st);
                }
                steps.Sort();
                float dist = Mathf.Abs(steps[steps.Count / 2] - 4f);
                if (outCount < bestOut || (outCount == bestOut && dist < bestDist)) { best = k; bestOut = outCount; bestDist = dist; }
            }
            return best * 12;
        }

        /// <summary>A stave note for a sounding pitch: key-signature spelling in Advanced, the
        /// pentatonic scale in Beginner. Out-of-range notes are folded by octaves into the stave.</summary>
        public static Note MakeNote(Score score, Clef clef, int midi, int tick, int len, bool tie, bool snapToScale = true)
        {
            Duration d; bool dotted;
            LengthToDuration(len, out d, out dotted);
            var n = new Note { tick = tick, duration = d, dotted = dotted, tie = tie };
            if (midi < 0) { n.isRest = true; n.tie = false; return n; }
            int acc; bool nat;
            int step = Score.StepFromMidi(clef, midi, score.keySignature, out acc, out nat);
            while (step > Score.MaxStep) { midi -= 12; step = Score.StepFromMidi(clef, midi, score.keySignature, out acc, out nat); }
            while (step < Score.MinStep) { midi += 12; step = Score.StepFromMidi(clef, midi, score.keySignature, out acc, out nat); }
            if (score.IsBeginner && snapToScale)
            {
                step = BeginnerKit.SnapToScale(score.Key, clef, step);
                acc = 0; nat = false;
            }
            n.step = step; n.accidental = acc; n.natural = nat;
            return n;
        }

        /// <summary>
        /// Writes quantised notes onto a staff from 'startTick' as ONE undo step, growing the piece
        /// (up to MaxBars) if needed. Returns how many sounding notes were placed.
        /// </summary>
        public static int Write(Score score, Staff staff, List<TickNote> notes, int startTick, bool replaceRange, bool saveUndo = true, bool snapToScale = true)
        {
            int tpb = score.TicksPerBar;
            int last = startTick;
            foreach (var t in notes) last = Mathf.Max(last, startTick + t.tick + t.length);
            int maxTicks = Score.MaxBars * tpb;

            if (saveUndo) score.SaveUndo();
            int neededBars = Mathf.Min(Score.MaxBars, Mathf.CeilToInt(last / (float)tpb));
            if (neededBars > score.bars) score.bars = neededBars;
            if (replaceRange) staff.notes.RemoveAll(n => n.tick >= startTick && n.tick < last);

            var list = new List<Note>();
            int placed = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                var t = notes[i];
                int tick = startTick + t.tick;
                if (tick + t.length > maxTicks) break;
                // a tie is only kept if the next piece really continues the same pitch
                bool tie = t.tieToNext && i + 1 < notes.Count && notes[i + 1].midi == t.midi && notes[i + 1].tick == t.tick + t.length;
                list.Add(MakeNote(score, staff.clef, t.midi, tick, t.length, tie, snapToScale));
                if (t.midi >= 0 && !(i > 0 && notes[i - 1].tieToNext && notes[i - 1].midi == t.midi)) placed++;
            }
            score.PlaceMany(staff, list, false);
            return placed;
        }
    }
}
