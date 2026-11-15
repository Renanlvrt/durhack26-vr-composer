using System;
using System.Collections.Generic;

namespace VRComposer
{
    /// <summary>
    /// Built-in demo pieces. Beethoven, Symphony No. 5 in C minor, Op. 67, I. Allegro con brio,
    /// bars 1-24 (the first two pages of the full score), arranged for string quartet: the four
    /// string parts are kept, the winds and double bass are left out, chords are reduced to what
    /// two hands per violin can play, and all ties, fermatas and dynamics are kept.
    /// </summary>
    public static class DemoScores
    {
        public const string Beethoven5Title = "Beethoven 5 (string quartet)";

        // Notation: bars separated by '|'. Token = pitch(es)/duration[.][~][:flags]
        //   pitch  = letter [# | b | n] octave, e.g. G4, F#4, Bn3 (natural), E4 (= Eb4 in C minor:
        //            the key signature applies). Chord: C4+G4+E5. Rest: r.
        //   duration = 1 2 4 8 16 (whole ... 16th), '.' dotted, '~' tie to the next note.
        //   flags  = F fermata, pp p f ff, sf, cr (cresc.), st staccato, ac accent.
        private static readonly string[] VlnI =
        {
            "r/8 G4/8:ff G4/8 G4/8", "E4/2:F", "r/8 F4/8 F4/8 F4/8", "D4/2~", "D4/2:F",
            "r/2", "r/2", "r/8 E5/8:p E5/8 E5/8", "C5/2~", "C5/4 r/4",
            "r/2", "r/8 F5/8 F5/8 F5/8", "D5/2~", "D5/8 G5/8 G5/8 F5/8", "E5/2",
            "D5/8 G5/8 G5/8 F5/8", "E5/2", "D5/8:cr G5/8 G5/8 F5/8", "E5/4:f r/4", "F#4+C5/4 r/4",
            "G5/2:F", "r/8 A4/8:ff A4/8 A4/8", "F4/2~", "F4/2:F",
        };

        private static readonly string[] VlnII =
        {
            "r/8 G4/8:ff G4/8 G4/8", "E4/2:F", "r/8 F4/8 F4/8 F4/8", "D4/2~", "D4/2:F",
            "r/8 G4/8:p G4/8 G4/8", "E4/2~", "E4/2~", "E4/2~", "E4/8 G4/8 G4/8 G4/8",
            "D4/2~", "D4/2", "G4/2~", "G4/2~", "G4/8 E4/8 E4/8 F4/8",
            "G4/2~", "G4/8 E4/8 E4/8 F4/8", "G4/8:cr D5/8 D5/8 G4/8", "C4+G4+E5/4:f r/4", "A3+E4/4 r/4",
            "D4+Bn4/4 r/4:F", "r/8 A4/8:ff A4/8 A4/8", "F4/2~", "F4/2:F",
        };

        private static readonly string[] Viola =
        {
            "r/8 G3/8:ff G3/8 G3/8", "E3/2:F", "r/8 F3/8 F3/8 F3/8", "D3/2~", "D3/2:F",
            "r/2", "r/8 A4/8:p A4/8 A4/8", "G4/2~", "G4/2~", "G4/4 r/4",
            "r/8 A4/8 A4/8 A4/8", "G4/2", "D4/2~", "D4/2", "E4/8 E4/8 E4/8 F4/8",
            "G4/2~", "G4/8 E4/8 E4/8 F4/8", "G4/4.:cr D4/8", "E4/4:f r/4", "A3/4 r/4",
            "G3/4 r/4:F", "r/8 A3/8:ff A3/8 A3/8", "F3/2~", "F3/2:F",
        };

        private static readonly string[] Cello =
        {
            "r/8 G2/8:ff G2/8 G2/8", "E2/2:F", "r/8 F2/8 F2/8 F2/8", "D2/2~", "D2/2:F",
            "r/2", "C4/2~:p", "C4/2~", "C4/2~", "C4/2",
            "Bn3/2~", "Bn3/2~", "Bn3/2~", "Bn3/2", "C4/2",
            "Bn3/2", "C4/2", "Bn3/8:cr Bn3/8 Bn3/8 Bn3/8", "C4/4:f r/4", "A2/4 r/4",
            "G2/4 r/4:F", "r/8 A2/8:ff A2/8 A2/8", "F2/2~", "F2/2:F",
        };

        /// <summary>Replaces the score with the Beethoven 5 quartet (Advanced mode, 2/4, C minor).</summary>
        public static void LoadBeethoven5(Score score)
        {
            score.SetMode(ComposeMode.Advanced);
            score.timeSigNumerator = 2;
            score.timeSigDenominator = 4;
            score.keySignature = -3;              // C minor
            score.keyIndex = 0;
            score.tempoBpm = 216;                 // Allegro con brio, half note = 108
            score.bars = VlnI.Length;
            score.backingChords = false;
            score.backingDrums = false;
            score.chords.Clear();
            score.staves.Clear();
            AddPart(score, "Violin I", Instruments.Find("Violin"), Clef.Treble, VlnI);
            AddPart(score, "Violin II", Instruments.Find("Violin"), Clef.Treble, VlnII);
            AddPart(score, "Viola", Instruments.Find("Viola"), Clef.Alto, Viola);
            AddPart(score, "Cello", Instruments.Find("Cello"), Clef.Bass, Cello);
            score.ClearUndo();
            score.NotifyChanged();
        }

        private static void AddPart(Score score, string name, InstrumentDef def, Clef clef, string[] bars)
        {
            var staff = new Staff { name = name, program = def.program, clef = clef };
            int tpb = score.TicksPerBar;
            for (int b = 0; b < bars.Length; b++)
            {
                int tick = b * tpb;
                foreach (var token in bars[b].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var notes = ParseToken(token, clef, tick);
                    staff.notes.AddRange(notes);
                    tick += notes[0].Length16;
                }
                if (tick != (b + 1) * tpb)
                    throw new FormatException(name + " bar " + (b + 1) + " has " + (tick - b * tpb) + " ticks, expected " + tpb);
            }
            staff.notes.Sort((a, c) => a.tick != c.tick ? a.tick.CompareTo(c.tick) : a.step.CompareTo(c.step));
            score.staves.Add(staff);
        }

        /// <summary>Parses one token into a note, a rest or a chord (several notes, same tick).</summary>
        public static List<Note> ParseToken(string token, Clef clef, int tick)
        {
            string flags = "";
            int colon = token.IndexOf(':');
            if (colon >= 0) { flags = token.Substring(colon + 1); token = token.Substring(0, colon); }
            int slash = token.IndexOf('/');
            string pitches = token.Substring(0, slash), dur = token.Substring(slash + 1);
            bool tie = dur.EndsWith("~");
            if (tie) dur = dur.Substring(0, dur.Length - 1);
            bool dotted = dur.EndsWith(".");
            if (dotted) dur = dur.Substring(0, dur.Length - 1);
            Duration d;
            switch (dur)
            {
                case "1": d = Duration.Whole; break;
                case "2": d = Duration.Half; break;
                case "4": d = Duration.Quarter; break;
                case "8": d = Duration.Eighth; break;
                case "16": d = Duration.Sixteenth; break;
                default: throw new FormatException("Bad duration in " + token);
            }

            var result = new List<Note>();
            foreach (var p in pitches.Split('+'))
            {
                var n = new Note { tick = tick, duration = d, dotted = dotted, tie = tie };
                if (p == "r") n.isRest = true;
                else
                {
                    int letter = "CDEFGAB".IndexOf(p[0]);
                    if (letter < 0) throw new FormatException("Bad pitch " + p);
                    int i = 1;
                    if (p[i] == '#') { n.accidental = 1; i++; }
                    else if (p[i] == 'b') { n.accidental = -1; i++; }
                    else if (p[i] == 'n') { n.natural = true; i++; }
                    int octave = int.Parse(p.Substring(i));
                    n.step = Score.StepFromDiatonic(clef, octave * 7 + letter);
                }
                result.Add(n);
            }
            if (result.Count > 0 && result[0].isRest) result[0].tie = false;

            // markings go on the first (lowest) note only; a chord shares them when it plays
            var first = result[0];
            foreach (var f in flags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                switch (f)
                {
                    case "F": first.fermata = true; break;
                    case "pp": first.dynamic = -2; break;
                    case "p": first.dynamic = -1; break;
                    case "f": first.dynamic = 1; break;
                    case "ff": first.dynamic = 2; break;
                    case "sf": first.sforzando = true; break;
                    case "cr": first.hairpin = 1; break;
                    case "st": foreach (var n in result) n.staccato = true; break;
                    case "ac": foreach (var n in result) n.accent = true; break;
                    default: throw new FormatException("Bad flag " + f + " in " + token);
                }
            return result;
        }
    }
}
