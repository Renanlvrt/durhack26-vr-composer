using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VRComposer
{
    /// <summary>Drop target for notes, rests, motifs and symbols: one staff row.</summary>
    public class StaffZone : MonoBehaviour, IPointerClickHandler
    {
        public Staff staff;
        public int staffIndex;
        public ScoreView view;

        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging) return;
            var app = VRComposerApp.Instance;
            if (app != null) { app.SelectStaff(staffIndex); app.Deselect(); }
        }
    }

    /// <summary>Drop target for chord blocks.</summary>
    public class ChordLaneZone : MonoBehaviour { public ScoreView view; }

    /// <summary>Drop target that deletes what is dropped on it.</summary>
    public class BinZone : MonoBehaviour { }

    /// <summary>
    /// The score panel, engraved like a hand-copied part: paper, Leland glyphs placed from SMuFL
    /// metrics, no permanent grid. Shows one page of bars (4 in Beginner, 2 in Advanced so 16ths
    /// have room), one row per staff, and the chord lane underneath.
    /// Row-local coordinates: x from the row's left edge, y = 0 at the row top, negative downward.
    /// </summary>
    public class ScoreView : MonoBehaviour
    {
        public const float LS = 22f;            // stave space in px (= mm)
        public const float Width = 1400f;
        public const float HeaderH = 66f;
        public const float RowH = 9.5f * LS;
        public const float LaneH = 72f;
        /// <summary>Where the first bar starts: after the clef, the key signature and the time signature.</summary>
        public float X0 { get { return 210f + KeySigWidth; } }
        public float KeySigWidth { get { int n = Mathf.Abs(app.Score.keySignature); return n == 0 ? 0f : n * 0.95f * LS + 0.6f * LS; } }
        public const float RightPad = 24f;
        public const float BarPad = 0.9f * LS;
        public const float StaffLeft = 100f;

        public Button PlayButton { get; private set; }
        public int pageStart;                    // first bar shown

        private VRComposerApp app;
        private RectTransform root, rows, lane, overlay;
        private TextMeshProUGUI header, readout, pageText;
        private Button prevPage, nextPage;
        private Image paper;
        private readonly List<StaffZone> zones = new List<StaffZone>();
        private readonly List<RectTransform> playheads = new List<RectTransform>();
        private readonly Dictionary<Note, GameObject> noteVisuals = new Dictionary<Note, GameObject>();
        private readonly Dictionary<ChordBlock, Image> chordVisuals = new Dictionary<ChordBlock, Image>();
        private Note playingNote, hoverNote;
        private string idleReadout = "";

        public float Height { get { return HeaderH + Mathf.Max(1, app.Score.staves.Count) * RowH + LaneH + 20f; } }
        // Advanced: 32 sixteenths per page (2 bars of 4/4, 4 bars of 2/4) so 16ths keep their room
        public int BarsPerPage { get { return Mathf.Min(app.Score.bars, app.Score.IsBeginner ? 4 : Mathf.Max(2, 32 / Mathf.Max(1, app.Score.TicksPerBar))); } }
        public int Unit { get { return app.Score.IsBeginner ? 2 : 1; } }      // ticks per horizontal slot
        public float BarW { get { return (Width - X0 - RightPad) / BarsPerPage; } }
        public int SlotsPerBar { get { return Mathf.Max(1, app.Score.TicksPerBar / Unit); } }
        public float SlotW { get { return (BarW - BarPad - 10f) / SlotsPerBar; } }
        public float BottomLineY { get { return -RowH * 0.5f - 2f * LS; } }
        public int PageCount { get { return Mathf.CeilToInt(app.Score.bars / (float)BarsPerPage); } }
        public IList<StaffZone> Zones { get { return zones; } }

        public float StepY(float step) { return BottomLineY + step * LS * 0.5f; }

        /// <summary>Row-local x of the notehead centre for a tick (must be on the current page).</summary>
        public float TickX(float tick)
        {
            int tpb = app.Score.TicksPerBar;
            int bar = Mathf.FloorToInt(tick / tpb);
            float within = tick - bar * tpb;
            return X0 + (bar - pageStart) * BarW + BarPad + (within / Unit) * SlotW + SlotW * 0.5f;
        }

        public bool OnPage(int tick)
        {
            int bar = tick / app.Score.TicksPerBar;
            return bar >= pageStart && bar < pageStart + BarsPerPage;
        }

        /// <summary>Continuous slot coordinate on this page (integer = a notehead centre).</summary>
        public float ContinuousSlot(float x)
        {
            int b = Mathf.Clamp(Mathf.FloorToInt((x - X0) / BarW), 0, BarsPerPage - 1);
            float within = (x - X0 - b * BarW - BarPad) / SlotW - 0.5f;
            within = Mathf.Clamp(within, -0.49f, SlotsPerBar - 0.51f);
            return b * SlotsPerBar + within;
        }

        public int SlotToTick(int slot)
        {
            return pageStart * app.Score.TicksPerBar + slot * Unit;
        }

        // ------------------------------------------------------------------ build

        public void Init(VRComposerApp owner, RectTransform canvasRoot)
        {
            app = owner;
            root = canvasRoot;
            paper = UIFactory.Img(root, "Paper", UIFactory.Paper, UIFactory.Rounded);
            UIFactory.Stretch(paper.rectTransform);

            header = UIFactory.Text(root, "", 24, UIFactory.Ink, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.PlaceTopLeft(header.rectTransform, 26, -16, 520, 36);

            readout = UIFactory.Text(root, "", 21, new Color(0.3f, 0.3f, 0.36f, 1f), TextAlignmentOptions.Center);
            UIFactory.PlaceTopLeft(readout.rectTransform, 540, -18, 470, 32);

            prevPage = UIFactory.Button(root, "<", () => SetPage(pageStart - BarsPerPage), UIFactory.Dark, 26);
            UIFactory.PlaceTopLeft((RectTransform)prevPage.transform, Width - 400, -12, 46, 44);
            pageText = UIFactory.Text(root, "", 19, UIFactory.Ink, TextAlignmentOptions.Center);
            UIFactory.PlaceTopLeft(pageText.rectTransform, Width - 350, -18, 120, 32);
            nextPage = UIFactory.Button(root, ">", () => SetPage(pageStart + BarsPerPage), UIFactory.Dark, 26);
            UIFactory.PlaceTopLeft((RectTransform)nextPage.transform, Width - 226, -12, 46, 44);

            PlayButton = UIFactory.Button(root, "Play", () => app.Player.TogglePlay(), UIFactory.Green, 24);
            UIFactory.PlaceTopLeft((RectTransform)PlayButton.transform, Width - 26 - 140, -12, 140, 44);

            rows = UIFactory.Rect("Staves", root);
            lane = UIFactory.Rect("Chord lane", root);
            overlay = UIFactory.Rect("Drag overlay", root);
            UIFactory.PlaceTopLeft(overlay, 0, 0, Width, 10);
        }

        public void SetPage(int start)
        {
            int maxStart = Mathf.Max(0, (PageCount - 1) * BarsPerPage);
            int s = Mathf.Clamp(start, 0, maxStart);
            s -= s % Mathf.Max(1, BarsPerPage);
            if (s == pageStart) return;
            pageStart = s;
            Rebuild();
        }

        public void ShowPageOf(int tick) { SetPage((tick / app.Score.TicksPerBar) / Mathf.Max(1, BarsPerPage) * BarsPerPage); }

        public void Rebuild()
        {
            var score = app.Score;
            if (pageStart >= score.bars || pageStart % Mathf.Max(1, BarsPerPage) != 0) pageStart = 0;

            root.sizeDelta = new Vector2(Width, Height);
            string keyText = score.IsBeginner ? score.Key.name + " pentatonic" : Score.KeySignatureName(score.keySignature);
            header.text = "Your score  ·  " + score.timeSigNumerator + "/" + score.timeSigDenominator + "  ·  " + keyText + "  ·  " + score.tempoBpm + " bpm";
            bool paged = PageCount > 1;
            prevPage.gameObject.SetActive(paged);
            nextPage.gameObject.SetActive(paged);
            pageText.gameObject.SetActive(paged);
            pageText.text = "Bars " + (pageStart + 1) + "-" + Mathf.Min(score.bars, pageStart + BarsPerPage) + " of " + score.bars;
            prevPage.interactable = pageStart > 0;
            nextPage.interactable = pageStart + BarsPerPage < score.bars;

            UIFactory.ClearChildren(rows);
            UIFactory.ClearChildren(lane);
            ClearOverlay();
            zones.Clear();
            playheads.Clear();
            noteVisuals.Clear();
            chordVisuals.Clear();
            playingNote = null;
            hoverNote = null;

            UIFactory.PlaceTopLeft(rows, 0, -HeaderH, Width, score.staves.Count * RowH);
            for (int i = 0; i < score.staves.Count; i++) BuildRow(score, i);

            UIFactory.PlaceTopLeft(lane, 0, -HeaderH - score.staves.Count * RowH - 4f, Width, LaneH);
            BuildChordLane(score);
            UIFactory.PlaceTopLeft(overlay, 0, 0, Width, 10);
            overlay.SetAsLastSibling();

            ApplySelectionVisual();
        }

        private void BuildRow(Score score, int index)
        {
            var staff = score.staves[index];
            var row = UIFactory.Rect("Staff " + (index + 1) + " · " + staff.name, rows);
            UIFactory.PlaceTopLeft(row, 0, -index * RowH, Width, RowH);

            var hit = UIFactory.HitArea(row, "Drop area");
            UIFactory.PlaceTopLeft(hit.rectTransform, StaffLeft - 10f, 0, Width - StaffLeft, RowH);
            var zone = row.gameObject.AddComponent<StaffZone>();
            zone.staff = staff;
            zone.staffIndex = index;
            zone.view = this;
            zones.Add(zone);

            bool selected = index == app.SelectedStaff && score.staves.Count > 1;
            Color ink = UIFactory.Ink;
            float line = Mathf.Max(2f, Smufl.StaffLineThickness * LS);

            // instrument name (selected staff marked with an orange bar)
            var name = UIFactory.Text(row, staff.name, 20, selected ? UIFactory.Orange : UIFactory.InkSoft, TextAlignmentOptions.Right, FontStyles.Bold);
            UIFactory.PlaceTopLeft(name.rectTransform, 4, StepY(4), 88, 30, new Vector2(0f, 0.5f));

            float right = Width - RightPad;
            for (int l = 0; l < 5; l++) HLine(row, StaffLeft, right, StepY(l * 2), line, ink);

            // clef and time signature
            if (staff.clef == Clef.Treble) RowGlyph(row, "gClef", StaffLeft + 0.6f * LS, StepY(2), ink);
            else if (staff.clef == Clef.Alto) RowGlyph(row, "cClef", StaffLeft + 0.6f * LS, StepY(4), ink);
            else RowGlyph(row, "fClef", StaffLeft + 0.6f * LS, StepY(6), ink);
            // key signature: flats B E A D G C F / sharps F C G D A E B at their usual heights
            int ks = score.keySignature;
            int[] treblePos = ks < 0 ? FlatSteps : SharpSteps;
            int shift = staff.clef == Clef.Bass ? -2 : staff.clef == Clef.Alto ? -1 : 0;
            for (int i = 0; i < Mathf.Abs(ks) && i < 7; i++)
                RowGlyph(row, ks < 0 ? "accidentalFlat" : "accidentalSharp", 174f + i * 0.95f * LS, StepY(treblePos[i] + shift), ink);
            RowGlyphCentred(row, "timeSig" + Mathf.Clamp(score.timeSigNumerator, 0, 9), X0 - 1.6f * LS, StepY(6), ink);
            RowGlyphCentred(row, "timeSig" + Mathf.Clamp(score.timeSigDenominator, 0, 9), X0 - 1.6f * LS, StepY(2), ink);

            // barlines (start of system, between bars, final double bar at the end of the piece)
            float thin = Mathf.Max(2f, Smufl.ThinBarlineThickness * LS), thick = Smufl.ThickBarlineThickness * LS;
            VLine(row, StaffLeft, StepY(0), StepY(8), thin, ink);
            for (int b = 1; b <= BarsPerPage; b++)
            {
                int bar = pageStart + b;            // barline after bar (bar-1)
                if (bar > score.bars) break;
                float x = X0 + b * BarW;
                if (bar == score.bars)
                {
                    VLine(row, x - thick / 2f, StepY(0), StepY(8), thick, ink);
                    VLine(row, x - thick - Smufl.ThinThickBarlineSeparation * LS - thin / 2f, StepY(0), StepY(8), thin, ink);
                }
                else VLine(row, x, StepY(0), StepY(8), thin, ink);
            }
            // bar numbers
            for (int b = 0; b < BarsPerPage && pageStart + b < score.bars; b++)
            {
                var num = UIFactory.Text(row, (pageStart + b + 1).ToString(), 15, UIFactory.InkSoft, TextAlignmentOptions.Left);
                UIFactory.PlaceTopLeft(num.rectTransform, X0 + b * BarW + 4f, StepY(8) + 30f, 40, 22, new Vector2(0f, 0.5f));
            }

            // notes on this page
            foreach (var n in staff.notes)
            {
                if (!OnPage(n.tick)) continue;
                BuildNote(row, zone, n, ink);
            }
            BuildTies(row, score, staff, ink);

            // playhead
            var ph = UIFactory.Img(row, "Playhead", UIFactory.Orange);
            UIFactory.PlaceTopLeft(ph.rectTransform, X0, StepY(10), 3f, 6f * LS, new Vector2(0.5f, 1f));
            ph.gameObject.SetActive(false);
            playheads.Add(ph.rectTransform);
        }

        private static readonly int[] FlatSteps = { 4, 7, 3, 6, 2, 5, 1 };    // treble: B4 E5 A4 D5 G4 C5 F4
        private static readonly int[] SharpSteps = { 8, 5, 9, 6, 3, 7, 4 };   // treble: F5 C5 G5 D5 A4 E5 B4

        /// <summary>Tie arcs: to the next note of the same pitch, or to the page edge when it is on the next page.</summary>
        private void BuildTies(RectTransform row, Score score, Staff staff, Color ink)
        {
            int first = pageStart * score.TicksPerBar, last = Mathf.Min(score.bars, pageStart + BarsPerPage) * score.TicksPerBar;
            float pageLeft = X0 + 0.3f * LS, pageRight = X0 + BarsPerPage * BarW - 0.3f * LS;
            foreach (var n in staff.notes)
            {
                if (n.isRest || !n.tie) continue;
                Note next = null;
                int midi = score.MidiOf(staff, n);
                foreach (var m in staff.notes)
                    if (!m.isRest && m.tick == n.End && score.MidiOf(staff, m) == midi) { next = m; break; }
                if (next == null) continue;
                bool startHere = n.tick >= first && n.tick < last, endHere = next.tick >= first && next.tick < last;
                if (!startHere && !endHere) continue;
                bool above = n.step >= 4;                      // opposite the stem
                float y = StepY(n.step) + (above ? 0.55f : -0.55f) * LS;
                float x0 = startHere ? TickX(n.tick) + 0.75f * LS : pageLeft;
                float x1 = endHere ? TickX(next.tick) - 0.75f * LS : pageRight;
                Engraver.Tie(row, x0, x1, y, above, LS, ink);
            }
        }

        private void BuildNote(RectTransform row, StaffZone zone, Note n, Color ink)
        {
            float x = TickX(n.tick);
            float y = n.isRest ? BottomLineY : StepY(n.step);
            var c = Engraver.Container(row, n.isRest ? "Rest" : "Note " + app.Score.NameOf(zone.staff.clef, n.step, n.accidental, n.natural), new Vector2(x, y));

            var halo = UIFactory.Img(c, "Halo", new Color(0, 0, 0, 0), UIFactory.Rounded);
            var hr = halo.rectTransform;
            hr.anchorMin = hr.anchorMax = new Vector2(0.5f, 0.5f);
            hr.anchoredPosition = n.isRest ? new Vector2(0, 2f * LS) : Vector2.zero;
            hr.sizeDelta = n.isRest ? new Vector2(2.4f * LS, 4f * LS) : new Vector2(2.4f * LS, 1.9f * LS);

            Engraver.Note(c, n, LS, ink);

            // grab area for move / select / delete
            var grab = UIFactory.HitArea(c, "Grab");
            var gr = grab.rectTransform;
            gr.anchorMin = gr.anchorMax = new Vector2(0.5f, 0.5f);
            gr.anchoredPosition = hr.anchoredPosition;
            gr.sizeDelta = new Vector2(Mathf.Max(1.8f * LS, SlotW * 0.95f), n.isRest ? 3.2f * LS : 1.7f * LS);
            var handle = grab.gameObject.AddComponent<NoteHandle>();
            handle.zone = zone;
            handle.note = n;

            noteVisuals[n] = c.gameObject;
        }

        private void BuildChordLane(Score score)
        {
            var bg = UIFactory.Img(lane, "Lane", new Color(0.08f, 0.08f, 0.1f, 0.05f), UIFactory.Rounded);
            UIFactory.PlaceTopLeft(bg.rectTransform, StaffLeft - 10f, -6f, Width - StaffLeft - RightPad + 10f, LaneH - 12f);
            var hit = UIFactory.HitArea(lane, "Chord drop area");
            UIFactory.PlaceTopLeft(hit.rectTransform, StaffLeft - 10f, 0, Width - StaffLeft, LaneH);
            lane.gameObject.AddComponent<ChordLaneZone>().view = this;

            var label = UIFactory.Text(lane, "Chords", 18, UIFactory.InkSoft, TextAlignmentOptions.Right, FontStyles.Bold);
            UIFactory.PlaceTopLeft(label.rectTransform, 4, -LaneH / 2f, 88, 28, new Vector2(0f, 0.5f));
            if (score.chords.Count == 0)
            {
                var empty = UIFactory.Text(lane, score.backingChords ? "Drop chord blocks here for a backing band" : "Backing is off", 17, UIFactory.InkSoft, TextAlignmentOptions.Left);
                UIFactory.PlaceTopLeft(empty.rectTransform, X0, -LaneH / 2f, 600, 28, new Vector2(0f, 0.5f));
            }

            foreach (var c in score.chords)
            {
                int startBar = c.tick / score.TicksPerBar;
                if (startBar < pageStart || startBar >= pageStart + BarsPerPage) continue;
                var block = ChordTile(lane, c.tick, c.length16, BeginnerKit.ChordName(score.Key, c.degree), BeginnerKit.ChordRoman(score.Key, c.degree), UIFactory.Indigo, true);
                chordVisuals[c] = block;
                var h = block.gameObject.AddComponent<ChordHandle>();
                h.chord = c;
            }
        }

        /// <summary>A chord block spanning [tick, tick+len) in the lane.</summary>
        public Image ChordTile(RectTransform parent, int tick, int len, string name, string roman, Color colour, bool raycast)
        {
            int tpb = app.Score.TicksPerBar;
            int bar = tick / tpb;
            float x0 = X0 + (bar - pageStart) * BarW + (tick - bar * tpb) / (float)tpb * BarW + 4f;
            float w = len / (float)tpb * BarW - 8f;
            var img = UIFactory.Img(parent, "Chord " + name, colour, UIFactory.Rounded, raycast);
            UIFactory.PlaceTopLeft(img.rectTransform, x0, -10f, w, LaneH - 20f);
            var t = UIFactory.Text(img.transform, name, 24, UIFactory.TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.Stretch(t.rectTransform, 12, 0, 8, 0);
            var r = UIFactory.Text(img.transform, roman, 16, UIFactory.TextDim, TextAlignmentOptions.Right);
            UIFactory.Stretch(r.rectTransform, 8, 0, 12, 0);
            return img;
        }

        // ------------------------------------------------------------------ helpers

        private static void HLine(RectTransform row, float x0, float x1, float y, float t, Color c)
        {
            var img = UIFactory.Img(row, "Line", c);
            UIFactory.PlaceTopLeft(img.rectTransform, x0, y, x1 - x0, t, new Vector2(0f, 0.5f));
        }

        private static void VLine(RectTransform row, float x, float y0, float y1, float t, Color c)
        {
            var img = UIFactory.Img(row, "Barline", c);
            UIFactory.PlaceTopLeft(img.rectTransform, x, y0, t, y1 - y0, new Vector2(0.5f, 0f));
        }

        private static void RowGlyph(RectTransform row, string glyph, float ox, float oy, Color c)
        {
            var cont = Engraver.Container(row, glyph, new Vector2(ox, oy));
            Engraver.Glyph(cont, glyph, 0, 0, LS, c);
        }

        private static void RowGlyphCentred(RectTransform row, string glyph, float cx, float cy, Color c)
        {
            var cont = Engraver.Container(row, glyph, new Vector2(cx, cy));
            Engraver.GlyphCentred(cont, glyph, 0, 0, LS, c);
        }

        // ------------------------------------------------------------------ overlays (drag feedback)

        private readonly List<GameObject> overlayItems = new List<GameObject>();

        public void ClearOverlay()
        {
            foreach (var g in overlayItems) if (g != null) Destroy(g);
            overlayItems.Clear();
        }

        private RectTransform RowRect(int staffIndex) { return zones[staffIndex].transform as RectTransform; }

        /// <summary>Ghost notes plus a soft column and a short pitch marker: the only grid ever shown.</summary>
        /// <param name="fit">Beginner only: colours the target box green / yellow / red. None = orange (Advanced).</param>
        public void ShowNoteGhost(int staffIndex, List<Note> notes, bool valid, Fit fit = Fit.None)
        {
            ClearOverlay();
            if (staffIndex < 0 || staffIndex >= zones.Count) return;
            var row = RowRect(staffIndex);
            Color baseCol = fit != Fit.None ? BeginnerKit.FitColour(fit) : valid ? UIFactory.Orange : UIFactory.Red;
            Color col = new Color(baseCol.r * 0.85f, baseCol.g * 0.85f, baseCol.b * 0.85f, valid ? 0.95f : 0.75f);

            if (notes.Count > 0)
            {
                // the target box over the time the notes cover (soft in Advanced, coloured in Beginner)
                int a = notes[0].tick, b = notes[notes.Count - 1].End;
                float x0 = TickX(a) - SlotW * 0.5f - 4f, x1 = TickX(b - 1) + SlotW * 0.5f + 4f;
                float w = Mathf.Max(1.6f * LS, x1 - x0);
                if (fit != Fit.None)
                {
                    Color ec = new Color(baseCol.r, baseCol.g, baseCol.b, 0.95f);
                    float top = StepY(11), h = 7f * LS, t = 4f;
                    var frame = new[] { new Vector4(x0 - t, top + t, w + 2 * t, t), new Vector4(x0 - t, top - h, w + 2 * t, t),
                                        new Vector4(x0 - t, top, t, h), new Vector4(x0 + w, top, t, h) };
                    foreach (var f in frame)
                    {
                        var edge = UIFactory.Img(row, "Fit box edge", ec);
                        UIFactory.PlaceTopLeft(edge.rectTransform, f.x, f.y, f.z, f.w);
                        overlayItems.Add(edge.gameObject);
                    }
                    // see-through fill: the stave lines and any notes already there stay visible
                    var fill = UIFactory.Img(row, "Fit box", new Color(1f, 1f, 1f, 0.3f), UIFactory.Rounded);
                    UIFactory.PlaceTopLeft(fill.rectTransform, x0, StepY(11), w, 7f * LS);
                    overlayItems.Add(fill.gameObject);
                    var tint = UIFactory.Img(row, "Fit tint", new Color(baseCol.r, baseCol.g, baseCol.b, 0.28f), UIFactory.Rounded);
                    UIFactory.PlaceTopLeft(tint.rectTransform, x0, StepY(11), w, 7f * LS);
                    overlayItems.Add(tint.gameObject);
                }
                else
                {
                    var colImg = UIFactory.Img(row, "Snap column", new Color(UIFactory.Orange.r, UIFactory.Orange.g, UIFactory.Orange.b, 0.07f), UIFactory.Rounded);
                    UIFactory.PlaceTopLeft(colImg.rectTransform, x0, StepY(10), w, 6f * LS);
                    overlayItems.Add(colImg.gameObject);
                }
            }

            foreach (var n in notes)
            {
                if (!n.isRest)
                {
                    // short pitch marker on the target line/space
                    bool onLine = Score.Mod(n.step, 2) == 0;
                    var mark = UIFactory.Img(row, "Pitch marker", new Color(baseCol.r, baseCol.g, baseCol.b, onLine ? 0.6f : 0.22f), onLine ? null : UIFactory.Rounded);
                    float w = 3.4f * LS;
                    UIFactory.PlaceTopLeft(mark.rectTransform, TickX(n.tick) - w / 2f, StepY(n.step), w, onLine ? 3f : LS * 0.9f, new Vector2(0f, 0.5f));
                    overlayItems.Add(mark.gameObject);
                }
                var c = Engraver.Container(row, "Ghost", new Vector2(TickX(n.tick), n.isRest ? BottomLineY : StepY(n.step)));
                Engraver.Note(c, n, LS, col);
                overlayItems.Add(c.gameObject);
            }
        }

        public void ShowChordGhost(int tick, int len, string name, string roman, Fit fit = Fit.None)
        {
            ClearOverlay();
            Color c = BeginnerKit.FitColour(fit);
            var img = ChordTile(lane, tick, len, name, roman, new Color(c.r, c.g, c.b, 0.88f), false);
            overlayItems.Add(img.gameObject);
        }

        public void ShowTargetNote(Note n)
        {
            ClearOverlay();
            GameObject go;
            if (n == null || !noteVisuals.TryGetValue(n, out go)) return;
            var halo = Instantiate(go.transform.Find("Halo").gameObject, go.transform);
            halo.name = "Target halo";
            halo.GetComponent<Image>().color = new Color(UIFactory.Cyan.r, UIFactory.Cyan.g, UIFactory.Cyan.b, 0.25f);
            halo.transform.SetAsFirstSibling();
            overlayItems.Add(halo);
        }

        public void SetReadout(string s) { if (readout != null && readout.text != s) readout.text = s; }
        public void SetIdleReadout(string s) { idleReadout = s; SetReadout(s); }
        public void RestoreReadout() { SetReadout(idleReadout); }

        // ------------------------------------------------------------------ hover / selection / playback

        public void SetHover(Note n, string text)
        {
            if (hoverNote == n) return;
            var old = hoverNote;
            hoverNote = n;
            Recolour(old);
            Recolour(n);
            if (n != null) SetReadout(text); else RestoreReadout();
        }

        public void ApplySelectionVisual()
        {
            foreach (var kv in noteVisuals) Recolour(kv.Key);
        }

        private void Recolour(Note n)
        {
            GameObject go;
            if (n == null || !noteVisuals.TryGetValue(n, out go) || go == null) return;
            bool sel = app.SelectedNote == n;
            Color c = sel ? UIFactory.Orange : n == playingNote ? UIFactory.Orange : n == hoverNote ? UIFactory.Cyan : UIFactory.Ink;
            Engraver.Recolour(go, c);
            var halo = go.transform.Find("Halo");
            if (halo != null) halo.GetComponent<Image>().color = sel ? UIFactory.OrangeSoft : new Color(0, 0, 0, 0);
        }

        private void Update()
        {
            if (app == null || app.Player == null) return;
            bool show = app.Player.IsPlaying;
            float beat = show ? app.Player.PositionBeats : 0f;
            float tick = beat * 4f;
            if (show && !OnPage(Mathf.FloorToInt(tick)) && app.followPlayhead) ShowPageOf(Mathf.FloorToInt(tick));
            bool onPage = OnPage(Mathf.FloorToInt(tick));
            for (int i = 0; i < playheads.Count; i++)
            {
                var ph = playheads[i];
                if (ph == null) continue;
                bool vis = show && onPage;
                if (ph.gameObject.activeSelf != vis) ph.gameObject.SetActive(vis);
                if (vis) ph.anchoredPosition = new Vector2(TickX(tick) - SlotW * 0.5f, StepY(10));
            }

            // highlight the note sounding on the selected staff
            Note now = null;
            if (show && app.Score.staves.Count > 0)
            {
                int t = Mathf.FloorToInt(tick);
                foreach (var st in app.Score.staves)
                {
                    foreach (var n in st.notes) if (!n.isRest && n.tick <= t && t < n.End) { now = n; break; }
                    if (now != null) break;
                }
            }
            if (now != playingNote)
            {
                var old = playingNote;
                playingNote = now;
                Recolour(old);
                Recolour(now);
            }
        }
    }
}
