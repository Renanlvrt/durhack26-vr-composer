using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VRComposer.Voice
{
    /// <summary>
    /// The client tools the ElevenLabs composer agent (Claude) calls. Notes never go through speech:
    /// the model writes text bars, they are validated here, and either placed as ONE undo step or
    /// sent back as a precise error so the model can fix them and call again.
    /// Runs on the main thread.
    /// </summary>
    public static class AgentTools
    {
        public struct Result { public string text; public bool error; }

        private static Result Ok(string s) { return new Result { text = s }; }
        private static Result Err(string s) { return new Result { text = s, error = true }; }

        public static Result Handle(string name, JObject args)
        {
            var app = VRComposerApp.Instance;
            if (app == null) return Err("The composer is not running.");
            try
            {
                switch (name)
                {
                    case "get_score": return Ok(ScoreText.Describe(app.Score, app.SelectedStaff));
                    case "write_bars": return WriteBars(app, args);
                    case "write_part": return WritePart(app, args);
                    case "start_singing":
                        return VoiceComposer.Instance != null ? Ok(VoiceComposer.Instance.StartSingingFromAgent()) : Err("Singing is not available.");
                    case "play": app.Player.Play(); return Ok("Playing.");
                    case "stop": app.Player.Stop(); return Ok("Stopped.");
                    case "undo":
                        if (!app.Score.CanUndo) return Err("Nothing to undo.");
                        app.Score.Undo(); app.Deselect(); return Ok("Undone.");
                    case "set_tempo":
                        app.Score.SetTempo(Int(args, "bpm", app.Score.tempoBpm));
                        return Ok("Tempo is now " + app.Score.tempoBpm + " bpm.");
                    default: return Err("Unknown tool " + name);
                }
            }
            catch (Exception e) { return Err(e.Message); }
        }

        private static string Str(JObject a, string k) { var t = a != null ? a[k] : null; return t == null || t.Type == JTokenType.Null ? null : t.ToString(); }
        private static int Int(JObject a, string k, int def)
        {
            var s = Str(a, k);
            int v;
            return s != null && int.TryParse(s, out v) ? v : def;
        }

        private static Staff FindStaff(Score s, string key, int selected)
        {
            if (string.IsNullOrWhiteSpace(key)) return s.staves.Count > 0 ? s.staves[Mathf.Clamp(selected, 0, s.staves.Count - 1)] : null;
            int idx;
            if (int.TryParse(key.Trim(), out idx) && idx >= 1 && idx <= s.staves.Count) return s.staves[idx - 1];
            foreach (var st in s.staves) if (string.Equals(st.name, key.Trim(), StringComparison.OrdinalIgnoreCase)) return st;
            foreach (var st in s.staves) if (st.name.IndexOf(key.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return st;
            return null;
        }

        /// <summary>write_bars(bars, chords?, staff?, start_bar?): appends after the last written bar.</summary>
        private static Result WriteBars(VRComposerApp app, JObject a)
        {
            var score = app.Score;
            var staff = FindStaff(score, Str(a, "staff"), app.SelectedStaff);
            if (staff == null) return Err("No staff called '" + Str(a, "staff") + "'. Staves: " + StaffList(score));
            int tpb = score.TicksPerBar;
            int startBar = Int(a, "start_bar", ScoreText.LastBar(score, staff) + 2) - 1;   // 1-based in, default = next bar
            if (startBar < 0) startBar = 0;
            return Place(app, staff, Str(a, "bars"), Str(a, "chords"), startBar, false, null);
        }

        /// <summary>write_part(instrument, bars, chords?, start_bar?): a new staff (or replace one) with a whole part.</summary>
        private static Result WritePart(VRComposerApp app, JObject a)
        {
            var score = app.Score;
            string inst = Str(a, "instrument");
            if (string.IsNullOrWhiteSpace(inst)) return Err("Say which instrument.");
            InstrumentDef def = default(InstrumentDef);
            bool found = false;
            foreach (var d in Instruments.All)
                if (string.Equals(d.name, inst.Trim(), StringComparison.OrdinalIgnoreCase) || (!found && d.name.IndexOf(inst.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
                { def = d; found = true; if (string.Equals(d.name, inst.Trim(), StringComparison.OrdinalIgnoreCase)) break; }
            if (!found)
            {
                var names = new List<string>();
                foreach (var d in Instruments.All) names.Add(d.name);
                return Err("Unknown instrument '" + inst + "'. Available: " + string.Join(", ", names));
            }
            int startBar = Math.Max(0, Int(a, "start_bar", 1) - 1);

            // validate first, so a bad part never adds an empty staff
            int barCount;
            try { ScoreText.ParseBars(Str(a, "bars"), score.TicksPerBar, out barCount); }
            catch (Exception e) { return Err(e.Message + ". Nothing was written; fix and call write_part again."); }

            var staff = FindStaff(score, def.name, -1);
            bool added = false;
            if (staff == null || ScoreText.LastBar(score, staff) >= 0 && staff.program != def.program)
            {
                if (score.staves.Count >= Score.MaxStaves) return Err("There are already " + Score.MaxStaves + " instruments (" + StaffList(score) + "). Ask the user which one to replace.");
                staff = score.AddStaff(def);       // this clears the undo history (existing behaviour)
                added = true;
            }
            var r = Place(app, staff, Str(a, "bars"), Str(a, "chords"), startBar, true, added ? "New staff " + def.name + ". " : null);
            if (!r.error) app.SelectStaff(score.staves.IndexOf(staff));
            return r;
        }

        private static Result Place(VRComposerApp app, Staff staff, string barsText, string chordsText, int startBar, bool replace, string prefix)
        {
            var score = app.Score;
            int tpb = score.TicksPerBar;
            int barCount;
            List<TickNote> notes;
            List<ChordBlock> chords;
            try
            {
                notes = ScoreText.ParseBars(barsText, tpb, out barCount);
                if (!string.IsNullOrWhiteSpace(chordsText) && !ScoreText.ChordLaneMatchesKey(score))
                    throw new FormatException("this score's key signature does not match the chord lane key (" + score.Key.name + "): leave chords empty");
                chords = ScoreText.ParseChords(score, chordsText, barCount);
            }
            catch (Exception e) { return Err(e.Message + ". Nothing was written; fix and call again."); }

            if (startBar + barCount > Score.MaxBars)
                return Err("That would go past bar " + Score.MaxBars + " (the maximum). Write at most " + Math.Max(0, Score.MaxBars - startBar) + " bars.");

            // range check (folded by octaves when placed, but tell the model)
            int folded = 0;
            foreach (var t in notes)
            {
                if (t.midi < 0) continue;
                int acc; bool nat;
                int st = Score.StepFromMidi(staff.clef, t.midi, score.keySignature, out acc, out nat);
                if (st < Score.MinStep || st > Score.MaxStep) folded++;
            }

            app.Player.Stop();
            int startTick = startBar * tpb;
            int placed = StaveWriter.Write(score, staff, notes, startTick, replace);   // SaveUndo inside
            foreach (var c in chords) score.PlaceChord(startTick + c.tick, c.length16, c.degree, false);
            app.Deselect();
            score.NotifyChanged();

            string msg = (prefix ?? "") + "Wrote " + placed + " notes on " + staff.name + ", bars " + (startBar + 1) + "-" + (startBar + barCount)
                + (chords.Count > 0 ? ", with " + chords.Count + " chords" : "") + ".";
            if (folded > 0) msg += " " + folded + " notes were outside the " + staff.name + " range and were moved by an octave.";
            if (score.IsBeginner) msg += " (Beginner mode: notes snap to the " + score.Key.name + " pentatonic scale.)";
            msg += " One Undo removes it.";
            return Ok(msg);
        }

        private static string StaffList(Score s)
        {
            var l = new List<string>();
            for (int i = 0; i < s.staves.Count; i++) l.Add((i + 1) + " " + s.staves[i].name);
            return string.Join(", ", l);
        }
    }
}
