using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRComposer.Voice
{
    /// <summary>
    /// The score as text for the composer agent, and the parser/validator for what it writes back.
    /// Pitches are always SOUNDING names (Eb4, F#5): the key signature is never implied, so the model
    /// cannot get it wrong. Durations: 1 2 4 8 16 (whole..16th), '.' dotted, '~' tied to the next
    /// note. Rest: r. Chord on one staff: C4+E4+G4/2. Bars are separated by '|'.
    /// </summary>
    public static class ScoreText
    {
        public static string Describe(Score s, int selectedStaff)
        {
            var sb = new StringBuilder();
            int tpb = s.TicksPerBar;
            bool flats = s.keySignature < 0;
            sb.Append("Mode: ").Append(s.IsBeginner ? "Beginner" : "Advanced");
            sb.Append(" | Time: ").Append(s.timeSigNumerator).Append('/').Append(s.timeSigDenominator)
              .Append(" (").Append(tpb).Append(" sixteenths per bar)");
            sb.Append(" | Tempo: ").Append(s.tempoBpm).Append(" bpm | Bars: ").Append(s.bars).Append(" (max ").Append(Score.MaxBars).Append(")\n");
            if (s.IsBeginner)
            {
                sb.Append("Key: ").Append(s.Key.name).Append(". Melody notes are snapped to its pentatonic scale: ");
                foreach (var l in BeginnerKit.PentatonicLetters(s.Key)) sb.Append(Score.LetterName(l)).Append(' ');
                sb.Append('\n');
            }
            else
                sb.Append("Key signature: ").Append(Score.KeySignatureName(s.keySignature)).Append(" (pitches below are sounding pitches)\n");
            sb.Append("Chord lane key: ").Append(s.Key.name).Append(". Allowed chords: ").Append(string.Join(" ", ChordNames(s))).Append('\n');
            if (!ChordLaneMatchesKey(s)) sb.Append("Note: the key signature does not match the chord lane key, so do not write chords.\n");

            for (int i = 0; i < s.staves.Count; i++)
            {
                var st = s.staves[i];
                sb.Append("\nStaff ").Append(i + 1).Append(" \"").Append(st.name).Append("\" (").Append(st.clef.ToString().ToLower())
                  .Append(" clef, range ").Append(Score.MidiName(s.MidiOf(st.clef, Score.MinStep), flats)).Append("..")
                  .Append(Score.MidiName(s.MidiOf(st.clef, Score.MaxStep), flats)).Append(')');
                if (i == selectedStaff) sb.Append(" [selected]");
                int last = LastBar(s, st);
                sb.Append(last < 0 ? " empty" : " written up to bar " + (last + 1)).Append('\n');
                for (int b = 0; b <= last; b++) sb.Append("  bar ").Append(b + 1).Append(": ").Append(BarText(s, st, b)).Append('\n');
            }
            sb.Append("\nChord lane: ");
            if (s.chords.Count == 0) sb.Append("empty");
            else
            {
                int lastChordBar = -1;
                foreach (var c in s.chords) lastChordBar = Mathf.Max(lastChordBar, (c.End - 1) / tpb);
                for (int b = 0; b <= lastChordBar; b++)
                {
                    sb.Append(b == 0 ? "" : " | ");
                    var parts = new List<string>();
                    foreach (var c in s.chords)
                        if (c.tick / tpb == b) parts.Add(BeginnerKit.ChordName(s.Key, c.degree) + "/" + LengthText(c.length16));
                    sb.Append(parts.Count == 0 ? "-" : string.Join(" ", parts));
                }
            }
            sb.Append('\n');
            return sb.ToString();
        }

        public static int LastBar(Score s, Staff st)
        {
            int last = -1;
            foreach (var n in st.notes) if (!n.isRest) last = Mathf.Max(last, (n.End - 1) / s.TicksPerBar);
            return last;
        }

        public static string BarText(Score s, Staff st, int bar)
        {
            int tpb = s.TicksPerBar, a = bar * tpb, z = a + tpb;
            bool flats = s.keySignature < 0;
            var groups = new SortedDictionary<int, List<Note>>();
            foreach (var n in st.notes)
                if (n.tick >= a && n.tick < z)
                {
                    List<Note> g;
                    if (!groups.TryGetValue(n.tick, out g)) groups[n.tick] = g = new List<Note>();
                    g.Add(n);
                }
            var tokens = new List<string>();
            int cursor = a;
            foreach (var kv in groups)
            {
                if (kv.Key > cursor) tokens.Add(RestText(kv.Key - cursor));
                var g = kv.Value;
                var first = g[0];
                if (first.isRest) tokens.Add("r/" + LengthText(first.Length16));
                else
                {
                    var names = new List<string>();
                    foreach (var n in g) if (!n.isRest) names.Add(Score.MidiName(s.MidiOf(st, n), flats));
                    tokens.Add(string.Join("+", names) + "/" + LengthText(first.Length16) + (first.tie ? "~" : ""));
                }
                cursor = Mathf.Max(cursor, kv.Key + first.Length16);
            }
            if (cursor < z) tokens.Add(RestText(z - cursor));
            return string.Join(" ", tokens);
        }

        private static string RestText(int len)
        {
            var parts = new List<TickNote>();
            RhythmQuantizer.Emit(parts, 0, len, -1, 64);
            var t = new List<string>();
            foreach (var p in parts) t.Add("r/" + LengthText(p.length));
            return string.Join(" ", t);
        }

        public static string LengthText(int len)
        {
            switch (len)
            {
                case 16: return "1";
                case 12: return "2.";
                case 8: return "2";
                case 6: return "4.";
                case 4: return "4";
                case 3: return "8.";
                case 2: return "8";
                case 1: return "16";
                default: return len + "?";
            }
        }

        // ------------------------------------------------------------------ parsing

        /// <summary>Sounding pitch name to MIDI: C4 = 60, Eb4, F#5, Bb3, also double sharps/flats.</summary>
        public static int ParsePitch(string p)
        {
            p = p.Trim();
            if (p.Length < 2) throw new FormatException("bad pitch '" + p + "'");
            int letter = "CDEFGAB".IndexOf(char.ToUpperInvariant(p[0]));
            if (letter < 0) throw new FormatException("bad pitch '" + p + "' (letter must be A-G)");
            int[] semis = { 0, 2, 4, 5, 7, 9, 11 };
            int i = 1, acc = 0;
            while (i < p.Length && (p[i] == '#' || p[i] == 'b' || p[i] == '♯' || p[i] == '♭' || p[i] == 'n'))
            {
                if (p[i] == '#' || p[i] == '♯') acc++;
                else if (p[i] == 'b' || p[i] == '♭') acc--;
                i++;
            }
            int octave;
            if (!int.TryParse(p.Substring(i), out octave)) throw new FormatException("bad pitch '" + p + "' (needs an octave number, e.g. C4)");
            return 12 * (octave + 1) + semis[letter] + acc;
        }

        public static int ParseLength(string d, string token)
        {
            bool dotted = d.EndsWith(".");
            if (dotted) d = d.Substring(0, d.Length - 1);
            int len;
            switch (d)
            {
                case "1": len = 16; break;
                case "2": len = 8; break;
                case "4": len = 4; break;
                case "8": len = 2; break;
                case "16": len = 1; break;
                default: throw new FormatException("bad duration in '" + token + "' (use 1 2 4 8 16, optional '.')");
            }
            if (dotted)
            {
                if (len == 1) throw new FormatException("a dotted 16th is not supported ('" + token + "')");
                len = len * 3 / 2;
            }
            return len;
        }

        /// <summary>
        /// Parses bars "C5/4 D5/4 E5/2 | F5/1" into ticks relative to the first bar. Every bar must be
        /// exactly full; errors name the bar and say what is wrong so the model can fix them.
        /// </summary>
        public static List<TickNote> ParseBars(string text, int ticksPerBar, out int barCount)
        {
            var result = new List<TickNote>();
            var errors = new List<string>();
            var bars = (text ?? "").Split('|');
            // allow a trailing '|'
            var list = new List<string>();
            foreach (var b in bars) if (b.Trim().Length > 0 || list.Count == 0) list.Add(b);
            if (list.Count > 1 && list[list.Count - 1].Trim().Length == 0) list.RemoveAt(list.Count - 1);
            barCount = list.Count;
            for (int b = 0; b < list.Count; b++)
            {
                int tick = b * ticksPerBar;
                foreach (var token in list[b].Split(new[] { ' ', '\t', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        int slash = token.LastIndexOf('/');
                        if (slash <= 0) throw new FormatException("'" + token + "' needs pitch/duration, e.g. C5/4 or r/8");
                        string pitches = token.Substring(0, slash), dur = token.Substring(slash + 1);
                        bool tie = dur.EndsWith("~");
                        if (tie) dur = dur.Substring(0, dur.Length - 1);
                        int len = ParseLength(dur, token);
                        if (pitches == "r" || pitches == "R")
                            result.Add(new TickNote { tick = tick, length = len, midi = -1 });
                        else
                            foreach (var p in pitches.Split('+'))
                                result.Add(new TickNote { tick = tick, length = len, midi = ParsePitch(p), tieToNext = tie });
                        tick += len;
                    }
                    catch (Exception e) { errors.Add("bar " + (b + 1) + ": " + e.Message); tick = int.MinValue / 2; break; }
                }
                int used = tick - b * ticksPerBar;
                if (tick >= 0 && used != ticksPerBar)
                    errors.Add("bar " + (b + 1) + " adds up to " + used + " sixteenths but a bar has " + ticksPerBar
                        + (used < ticksPerBar ? " (add " + (ticksPerBar - used) + " more, e.g. a rest)" : " (remove " + (used - ticksPerBar) + ")"));
            }
            if (errors.Count > 0) throw new FormatException(string.Join("; ", errors));
            return result;
        }

        // ------------------------------------------------------------------ chords

        public static List<string> ChordNames(Score s)
        {
            var l = new List<string>();
            for (int d = 0; d < 7; d++) l.Add(BeginnerKit.ChordName(s.Key, d));
            return l;
        }

        /// <summary>The chord lane plays in Score.Key; in Advanced the key signature must agree with it.</summary>
        public static bool ChordLaneMatchesKey(Score s)
        {
            if (s.IsBeginner) return true;
            // fifths of the relative major of each chord-lane key: C 0, G +1, F -1, (A minor = C) 0
            int fifths = s.Key.majorTonicLetter == 4 ? 1 : s.Key.majorTonicLetter == 3 ? -1 : 0;
            return s.keySignature == fifths;
        }

        /// <summary>Parses "C/2 G/2 | Am/1 | - | F/1" into (tick, length, degree) per bar.</summary>
        public static List<ChordBlock> ParseChords(Score s, string text, int barCount)
        {
            var result = new List<ChordBlock>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            int tpb = s.TicksPerBar;
            var names = ChordNames(s);
            var errors = new List<string>();
            var bars = text.Split('|');
            for (int b = 0; b < bars.Length && b < Math.Max(barCount, bars.Length); b++)
            {
                int tick = b * tpb;
                foreach (var token in bars[b].Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token == "-") { tick = b * tpb + tpb; continue; }
                    try
                    {
                        int slash = token.LastIndexOf('/');
                        string name = slash > 0 ? token.Substring(0, slash) : token;
                        int len = slash > 0 ? ParseLength(token.Substring(slash + 1), token) : tpb - (tick - b * tpb);
                        int deg = names.FindIndex(n => string.Equals(n, Simplify(name), StringComparison.OrdinalIgnoreCase));
                        if (deg < 0) throw new FormatException("chord '" + name + "' is not in " + s.Key.name + " (allowed: " + string.Join(" ", names) + ")");
                        if (tick + len > (b + 1) * tpb) throw new FormatException("chords in bar " + (b + 1) + " are longer than the bar");
                        result.Add(new ChordBlock { tick = tick, length16 = len, degree = deg });
                        tick += len;
                    }
                    catch (Exception e) { errors.Add("chords bar " + (b + 1) + ": " + e.Message); break; }
                }
            }
            if (errors.Count > 0) throw new FormatException(string.Join("; ", errors));
            return result;
        }

        // G7 -> G, Cmaj7 -> C, Am7 -> Am, Bm7b5 -> Bdim, Bo -> Bdim
        private static string Simplify(string n)
        {
            n = n.Trim();
            int i = 1;
            if (n.Length > 1 && (n[1] == '#' || n[1] == 'b')) i = 2;
            string root = n.Substring(0, Math.Min(i, n.Length)), q = n.Substring(Math.Min(i, n.Length));
            if (q.StartsWith("dim") || q.StartsWith("o") || q.StartsWith("°") || q.Contains("b5")) return root + "dim";
            if (q.StartsWith("maj") || q.StartsWith("M")) return root;
            if (q.StartsWith("m")) return root + "m";
            return root;
        }
    }
}
