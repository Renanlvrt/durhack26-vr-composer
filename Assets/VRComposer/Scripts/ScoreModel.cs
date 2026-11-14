using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRComposer
{
    public enum Clef { Treble, Bass }
    public enum ComposeMode { Beginner, Advanced }

    /// <summary>Note values, measured in 16th-note ticks.</summary>
    public enum Duration { Whole = 16, Half = 8, Quarter = 4, Eighth = 2, Sixteenth = 1 }

    public enum SymbolKind { Sharp, Flat, Natural, Dot, Staccato, Accent, Piano, Forte, TrebleClef, BassClef }

    public static class Durations
    {
        public static string Name(Duration d, bool rest)
        {
            string n;
            switch (d)
            {
                case Duration.Whole: n = "Whole"; break;
                case Duration.Half: n = "Half"; break;
                case Duration.Quarter: n = "Quarter"; break;
                case Duration.Eighth: n = "Eighth"; break;
                default: n = "16th"; break;
            }
            return rest ? n + " rest" : n;
        }

        /// <summary>Snap grid for a note length: quarters on beats, eighths on 8ths, and so on.</summary>
        public static int Grid(int length16)
        {
            int g = 1;
            while (g * 2 <= length16 && g < 4) g *= 2;
            return g;
        }
    }

    [Serializable]
    public class Note
    {
        public int tick;               // start, in 16ths from the start of the piece
        public Duration duration;
        public bool dotted;
        public bool isRest;
        public int step;               // stave position: 0 = bottom line, 8 = top line
        public int accidental;         // -1 flat, 0 none, +1 sharp
        public bool natural;           // show a natural sign
        public bool staccato;
        public bool accent;
        public int dynamic;            // -1 p, 0 default, +1 f

        public int Length16 { get { return dotted ? (int)duration * 3 / 2 : (int)duration; } }
        public int End { get { return tick + Length16; } }
        public Note Clone() { return (Note)MemberwiseClone(); }
    }

    [Serializable]
    public class Staff
    {
        public string name;
        public int program;            // General MIDI program, 0-based
        public Clef clef;
        public List<Note> notes = new List<Note>();

        public Staff Clone()
        {
            var s = (Staff)MemberwiseClone();
            s.notes = new List<Note>();
            foreach (var n in notes) s.notes.Add(n.Clone());
            return s;
        }
    }

    /// <summary>A chord in the chord lane (plays as backing). degree = scale degree 0..6 of the major key.</summary>
    [Serializable]
    public class ChordBlock
    {
        public int tick;
        public int length16;
        public int degree;
        public int End { get { return tick + length16; } }
        public ChordBlock Clone() { return (ChordBlock)MemberwiseClone(); }
    }

    public struct InstrumentDef
    {
        public string name;
        public int program;
        public Clef clef;
        public InstrumentDef(string name, int program, Clef clef) { this.name = name; this.program = program; this.clef = clef; }
    }

    public static class Instruments
    {
        // General MIDI program numbers (0-based).
        public static readonly InstrumentDef[] All =
        {
            new InstrumentDef("Flute", 73, Clef.Treble),
            new InstrumentDef("Alto Sax", 65, Clef.Treble),
            new InstrumentDef("Violin", 40, Clef.Treble),
            new InstrumentDef("Clarinet", 71, Clef.Treble),
            new InstrumentDef("Oboe", 68, Clef.Treble),
            new InstrumentDef("Trumpet", 56, Clef.Treble),
            new InstrumentDef("French Horn", 60, Clef.Treble),
            new InstrumentDef("Harp", 46, Clef.Treble),
            new InstrumentDef("Piano", 0, Clef.Treble),
            new InstrumentDef("Marimba", 12, Clef.Treble),
            new InstrumentDef("Strings", 48, Clef.Treble),
            new InstrumentDef("Cello", 42, Clef.Bass),
            new InstrumentDef("Bassoon", 70, Clef.Bass),
            new InstrumentDef("Trombone", 57, Clef.Bass),
            new InstrumentDef("Tuba", 58, Clef.Bass),
        };

        public static int IndexOfProgram(int program)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].program == program) return i;
            return 0;
        }
    }

    /// <summary>Keys offered in Beginner mode. Every one of these pentatonic scales uses only natural notes.</summary>
    public struct KeyDef
    {
        public string name;
        public int tonicLetter;        // 0 = C ... 6 = B
        public int majorTonicLetter;   // letter of the relative major (for chords)
        public bool minor;
        public KeyDef(string name, int tonic, int majorTonic, bool minor) { this.name = name; tonicLetter = tonic; majorTonicLetter = majorTonic; this.minor = minor; }
    }

    /// <summary>
    /// The piece being composed. Plain C#: the UI edits it, the player reads it.
    /// Time is in integer 16th-note ticks so snapping is exact. Only touch it from the main thread.
    /// </summary>
    public class Score
    {
        public const int MaxStaves = 4;
        public const int MinStep = -4;   // treble: A3, bass: C2
        public const int MaxStep = 12;   // treble: C6, bass: E4
        public const int MinBars = 1;
        public const int MaxBars = 16;

        public static readonly int[][] TimeSignatures =
        {
            new[] { 4, 4 }, new[] { 3, 4 }, new[] { 2, 4 }, new[] { 6, 8 },
        };

        public static readonly KeyDef[] Keys =
        {
            new KeyDef("C major", 0, 0, false),
            new KeyDef("G major", 4, 4, false),
            new KeyDef("F major", 3, 3, false),
            new KeyDef("A minor", 5, 0, true),
        };

        public ComposeMode mode = ComposeMode.Beginner;
        public int keyIndex;
        public int timeSigNumerator = 4;
        public int timeSigDenominator = 4;
        public int bars = 4;
        public int tempoBpm = 96;
        public bool backingChords = true;
        public bool backingDrums;
        public readonly List<Staff> staves = new List<Staff>();
        public readonly List<ChordBlock> chords = new List<ChordBlock>();

        /// <summary>Raised after any edit. The view and the player listen to it.</summary>
        public event Action Changed;

        public KeyDef Key { get { return Keys[Mathf.Clamp(keyIndex, 0, Keys.Length - 1)]; } }
        public int TicksPerBar { get { return timeSigNumerator * 16 / timeSigDenominator; } }
        public int TotalTicks { get { return bars * TicksPerBar; } }
        public float BeatsPerBar { get { return TicksPerBar / 4f; } }
        public bool IsBeginner { get { return mode == ComposeMode.Beginner; } }

        public void NotifyChanged() { if (Changed != null) Changed(); }

        // ---------------------------------------------------------------- undo

        private class Snapshot
        {
            public List<Staff> staves;
            public List<ChordBlock> chords;
            public int bars;
        }

        private readonly List<Snapshot> undo = new List<Snapshot>();
        public bool CanUndo { get { return undo.Count > 0; } }

        /// <summary>Call before every edit that should be undoable.</summary>
        public void SaveUndo()
        {
            var snap = new Snapshot { staves = new List<Staff>(), chords = new List<ChordBlock>(), bars = bars };
            foreach (var s in staves) snap.staves.Add(s.Clone());
            foreach (var c in chords) snap.chords.Add(c.Clone());
            undo.Add(snap);
            if (undo.Count > 64) undo.RemoveAt(0);
        }

        public void Undo()
        {
            if (undo.Count == 0) return;
            var snap = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            // Keep the Staff objects (views hold references); restore their content.
            for (int i = 0; i < staves.Count && i < snap.staves.Count; i++)
            {
                staves[i].notes = snap.staves[i].notes;
                staves[i].name = snap.staves[i].name;
                staves[i].program = snap.staves[i].program;
                staves[i].clef = snap.staves[i].clef;
            }
            chords.Clear();
            chords.AddRange(snap.chords);
            bars = snap.bars;
            NotifyChanged();
        }

        // ---------------------------------------------------------------- settings

        public Staff AddStaff(InstrumentDef def)
        {
            if (staves.Count >= MaxStaves) return null;
            var s = new Staff { name = def.name, program = def.program, clef = def.clef };
            staves.Add(s);
            undo.Clear(); // staff list changed: older snapshots no longer line up
            NotifyChanged();
            return s;
        }

        public void RemoveStaff(Staff s)
        {
            if (staves.Count <= 1) { SaveUndo(); s.notes.Clear(); NotifyChanged(); return; }
            staves.Remove(s);
            undo.Clear();
            NotifyChanged();
        }

        public void SetInstrument(Staff s, InstrumentDef def)
        {
            SaveUndo();
            s.name = def.name;
            s.program = def.program;
            SetClefKeepPitch(s, def.clef, false);
            NotifyChanged();
        }

        /// <summary>Changes the clef and moves every note so it keeps sounding the same pitch.</summary>
        public void SetClefKeepPitch(Staff s, Clef clef, bool notify = true)
        {
            if (s.clef != clef)
            {
                int shift = BottomLineDiatonic(s.clef) - BottomLineDiatonic(clef);
                foreach (var n in s.notes)
                    if (!n.isRest) n.step = Mathf.Clamp(n.step + shift, MinStep, MaxStep);
                s.clef = clef;
            }
            if (notify) NotifyChanged();
        }

        public void SetMode(ComposeMode m)
        {
            mode = m;
            if (m == ComposeMode.Beginner) { timeSigNumerator = 4; timeSigDenominator = 4; }
            NotifyChanged();
        }

        public void CycleKey() { keyIndex = (keyIndex + 1) % Keys.Length; NotifyChanged(); }

        public void CycleTimeSignature()
        {
            int i = 0;
            for (; i < TimeSignatures.Length; i++)
                if (TimeSignatures[i][0] == timeSigNumerator && TimeSignatures[i][1] == timeSigDenominator) break;
            var next = TimeSignatures[(i + 1) % TimeSignatures.Length];
            SaveUndo();
            timeSigNumerator = next[0];
            timeSigDenominator = next[1];
            TrimToLength();
            NotifyChanged();
        }

        public void SetBars(int value) { SaveUndo(); bars = Mathf.Clamp(value, MinBars, MaxBars); TrimToLength(); NotifyChanged(); }
        public void SetTempo(int value) { tempoBpm = Mathf.Clamp(value, 40, 220); NotifyChanged(); }

        private void TrimToLength()
        {
            int total = TotalTicks;
            foreach (var s in staves) s.notes.RemoveAll(n => n.tick >= total);
            chords.RemoveAll(c => c.tick >= total);
            foreach (var c in chords) c.length16 = Mathf.Min(c.length16, total - c.tick);
        }

        // ---------------------------------------------------------------- editing

        /// <summary>True if the note would cross a barline (not allowed: no ties).</summary>
        public bool CrossesBar(int tick, int length16)
        {
            int bar = tick / TicksPerBar;
            return tick + length16 > (bar + 1) * TicksPerBar || tick + length16 > TotalTicks;
        }

        /// <summary>
        /// Puts a note or rest on a staff. Anything it overlaps is replaced, except notes that start
        /// together with the same length (they form a chord). One voice per staff keeps the
        /// engraving clean and the timing exact.
        /// </summary>
        public bool Place(Staff s, Note n, bool saveUndo = true)
        {
            if (n.tick < 0 || CrossesBar(n.tick, n.Length16)) return false;
            if (saveUndo) SaveUndo();
            RemoveOverlaps(s, n);
            s.notes.Add(n);
            s.notes.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.step.CompareTo(b.step));
            NotifyChanged();
            return true;
        }

        private static void RemoveOverlaps(Staff s, Note n)
        {
            s.notes.RemoveAll(o =>
            {
                bool overlap = o.tick < n.End && n.tick < o.End;
                if (!overlap) return false;
                bool chordMate = !n.isRest && !o.isRest && o.tick == n.tick && o.Length16 == n.Length16 && o.step != n.step;
                return !chordMate;
            });
        }

        /// <summary>Places several notes as one undoable edit (a motif).</summary>
        public void PlaceMany(Staff s, List<Note> notes)
        {
            SaveUndo();
            foreach (var n in notes)
            {
                if (n.tick < 0 || CrossesBar(n.tick, n.Length16)) continue;
                RemoveOverlaps(s, n);
                s.notes.Add(n);
            }
            s.notes.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.step.CompareTo(b.step));
            NotifyChanged();
        }

        /// <summary>Moves a note: one undo step. Returns false (and leaves it) if it does not fit.</summary>
        public bool Move(Staff from, Note n, Staff to, int tick, int step)
        {
            var moved = n.Clone();
            moved.tick = tick;
            if (!moved.isRest) moved.step = step;
            if (CrossesBar(moved.tick, moved.Length16)) return false;
            SaveUndo();
            from.notes.Remove(n);
            RemoveOverlaps(to, moved);
            to.notes.Add(moved);
            to.notes.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.step.CompareTo(b.step));
            NotifyChanged();
            return true;
        }

        /// <summary>Edits a note in place (thumbstick nudge). Returns false if the new spot does not fit.</summary>
        public bool Nudge(Staff s, Note n, int tick, int step)
        {
            if (tick < 0 || CrossesBar(tick, n.Length16)) return false;
            if (!n.isRest && (step < MinStep || step > MaxStep)) return false;
            SaveUndo();
            s.notes.Remove(n);
            n.tick = tick;
            if (!n.isRest) n.step = step;
            RemoveOverlaps(s, n);
            s.notes.Add(n);
            s.notes.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : a.step.CompareTo(b.step));
            NotifyChanged();
            return true;
        }

        public void Remove(Staff s, Note n)
        {
            if (!s.notes.Contains(n)) return;
            SaveUndo();
            s.notes.Remove(n);
            NotifyChanged();
        }

        public bool ApplySymbol(Staff s, Note n, SymbolKind k)
        {
            if (k == SymbolKind.TrebleClef || k == SymbolKind.BassClef)
            {
                SaveUndo();
                SetClefKeepPitch(s, k == SymbolKind.TrebleClef ? Clef.Treble : Clef.Bass);
                return true;
            }
            if (n == null) return false;
            if (n.isRest && k != SymbolKind.Dot) return false;
            SaveUndo();
            switch (k)
            {
                case SymbolKind.Sharp: n.accidental = n.accidental == 1 ? 0 : 1; n.natural = false; break;
                case SymbolKind.Flat: n.accidental = n.accidental == -1 ? 0 : -1; n.natural = false; break;
                case SymbolKind.Natural: n.accidental = 0; n.natural = !n.natural; break;
                case SymbolKind.Staccato: n.staccato = !n.staccato; break;
                case SymbolKind.Accent: n.accent = !n.accent; break;
                case SymbolKind.Piano: n.dynamic = n.dynamic == -1 ? 0 : -1; break;
                case SymbolKind.Forte: n.dynamic = n.dynamic == 1 ? 0 : 1; break;
                case SymbolKind.Dot:
                    if (!n.dotted && (n.duration == Duration.Whole || n.duration == Duration.Sixteenth)) { undo.RemoveAt(undo.Count - 1); return false; }
                    n.dotted = !n.dotted;
                    if (n.dotted && CrossesBar(n.tick, n.Length16)) { n.dotted = false; undo.RemoveAt(undo.Count - 1); return false; }
                    if (n.dotted) foreach (var st in staves) if (st.notes.Contains(n)) { st.notes.Remove(n); RemoveOverlaps(st, n); st.notes.Add(n); st.notes.Sort((a, b) => a.tick.CompareTo(b.tick)); }
                    break;
            }
            NotifyChanged();
            return true;
        }

        public void PlaceChord(int tick, int length16, int degree, bool saveUndo = true)
        {
            length16 = Mathf.Min(length16, TotalTicks - tick);
            if (tick < 0 || length16 <= 0) return;
            if (saveUndo) SaveUndo();
            chords.RemoveAll(c => c.tick < tick + length16 && tick < c.End);
            chords.Add(new ChordBlock { tick = tick, length16 = length16, degree = degree });
            chords.Sort((a, b) => a.tick.CompareTo(b.tick));
            NotifyChanged();
        }

        public void RemoveChord(ChordBlock c)
        {
            if (!chords.Contains(c)) return;
            SaveUndo();
            chords.Remove(c);
            NotifyChanged();
        }

        public ChordBlock ChordAt(int tick)
        {
            foreach (var c in chords) if (c.tick <= tick && tick < c.End) return c;
            return null;
        }

        public void ClearNotes()
        {
            SaveUndo();
            foreach (var s in staves) s.notes.Clear();
            chords.Clear();
            NotifyChanged();
        }

        // ---------------------------------------------------------------- pitch helpers

        private static readonly int[] Semitones = { 0, 2, 4, 5, 7, 9, 11 };
        private static readonly string[] Letters = { "C", "D", "E", "F", "G", "A", "B" };

        public static int FloorDiv(int a, int b) { return (int)Math.Floor(a / (double)b); }
        public static int Mod(int a, int b) { return ((a % b) + b) % b; }

        /// <summary>Diatonic index (octave * 7 + letter) of the bottom staff line: E4 treble, G2 bass.</summary>
        public static int BottomLineDiatonic(Clef c) { return c == Clef.Treble ? 4 * 7 + 2 : 2 * 7 + 4; }

        public static int Diatonic(Clef clef, int step) { return BottomLineDiatonic(clef) + step; }
        public static int StepFromDiatonic(Clef clef, int diatonic) { return diatonic - BottomLineDiatonic(clef); }

        public static int DiatonicToMidi(int d)
        {
            int octave = FloorDiv(d, 7);
            return 12 * (octave + 1) + Semitones[Mod(d, 7)];
        }

        public static int StepToMidi(Clef clef, int step, int accidental = 0)
        {
            return DiatonicToMidi(Diatonic(clef, step)) + accidental;
        }

        public static string StepToName(Clef clef, int step, int accidental = 0)
        {
            int d = Diatonic(clef, step);
            string acc = accidental > 0 ? "#" : accidental < 0 ? "b" : "";
            return Letters[Mod(d, 7)] + acc + FloorDiv(d, 7);
        }

        public static string LetterName(int letter) { return Letters[Mod(letter, 7)]; }
    }
}
