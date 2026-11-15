using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VRComposer
{
    public enum DragKind { Note, Rest, Symbol, Motif, Chord, MoveNote, MoveChord }

    /// <summary>What is being dragged.</summary>
    public class DragPayload
    {
        public DragKind kind;
        public Duration duration;
        public bool dotted;
        public SymbolKind symbol;
        public int motif;
        public int chordDegree;
        public Staff fromStaff;
        public Note note;            // MoveNote
        public ChordBlock chord;     // MoveChord

        public int Length16
        {
            get
            {
                if (kind == DragKind.Motif) return BeginnerKit.Motifs[motif].Length16;
                if (kind == DragKind.MoveNote) return note.Length16;
                return dotted ? (int)duration * 3 / 2 : (int)duration;
            }
        }

        public bool IsNoteLike { get { return kind == DragKind.Note || kind == DragKind.Rest || kind == DragKind.Motif || kind == DragKind.MoveNote; } }
        public bool IsRest { get { return kind == DragKind.Rest || (kind == DragKind.MoveNote && note.isRest); } }
    }

    /// <summary>1€ filter (Casiez et al. 2012): smooths hand tremor, stays responsive on fast moves.</summary>
    public class OneEuro2D
    {
        public float minCutoff = 1.2f, beta = 0.004f, dCutoff = 1f;
        private Vector2 x, dx;
        private bool init;

        public void Reset() { init = false; }

        public Vector2 Filter(Vector2 v, float dt)
        {
            if (!init || dt <= 0f) { init = true; x = v; dx = Vector2.zero; return v; }
            Vector2 d = (v - x) / dt;
            dx = Vector2.Lerp(dx, d, Alpha(dCutoff, dt));
            float cutoff = minCutoff + beta * dx.magnitude;
            x = Vector2.Lerp(x, v, Alpha(cutoff, dt));
            return x;
        }

        private static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }

    /// <summary>Snap state for one drag: sticky (hysteresis) so the target does not flicker.</summary>
    public class StickySnap
    {
        public const float SlotMargin = 0.3f;     // extra fraction of a cell needed to leave the current one
        public const float StepMargin = 0.35f;    // in half-spaces
        public int cell = int.MinValue;
        public int step = int.MinValue;

        public void Reset() { cell = int.MinValue; step = int.MinValue; }

        public int Cell(float continuous)
        {
            int cand = Mathf.RoundToInt(continuous);
            if (cell != int.MinValue && Mathf.Abs(continuous - cell) < 0.5f + SlotMargin) return cell;
            cell = cand;
            return cell;
        }

        /// <summary>Snaps a continuous step to the nearest allowed step, keeping the current one until clearly passed.</summary>
        public int Step(float v, Func<int, bool> allowed)
        {
            int nearest = NearestAllowed(v, allowed);
            if (step != int.MinValue && allowed(step))
            {
                int below = NextAllowed(step, -1, allowed), above = NextAllowed(step, 1, allowed);
                float lo = below == int.MinValue ? float.NegativeInfinity : (below + step) * 0.5f - StepMargin;
                float hi = above == int.MinValue ? float.PositiveInfinity : (above + step) * 0.5f + StepMargin;
                if (v >= lo && v <= hi) return step;
            }
            step = nearest;
            return step;
        }

        private static int NearestAllowed(float v, Func<int, bool> allowed)
        {
            int c = Mathf.Clamp(Mathf.RoundToInt(v), Score.MinStep, Score.MaxStep);
            for (int off = 0; off <= 16; off++)
            {
                int a = c + off, b = c - off;
                bool okA = a <= Score.MaxStep && allowed(a), okB = b >= Score.MinStep && allowed(b);
                if (okA && okB) return Mathf.Abs(a - v) <= Mathf.Abs(b - v) ? a : b;
                if (okA) return a;
                if (okB) return b;
            }
            return c;
        }

        private static int NextAllowed(int from, int dir, Func<int, bool> allowed)
        {
            for (int s = from + dir; s >= Score.MinStep && s <= Score.MaxStep; s += dir) if (allowed(s)) return s;
            return int.MinValue;
        }
    }

    /// <summary>Floating copy of the dragged item when it is not over the stave.</summary>
    public class DragGhost : MonoBehaviour
    {
        private static DragGhost instance;
        private RectTransform glyphRoot;
        private TextMeshProUGUI label;
        private Image chip;

        private static DragGhost Get()
        {
            if (instance != null) return instance;
            var go = new GameObject("VRComposer Drag Ghost", typeof(RectTransform));
            go.layer = UIFactory.UILayer;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(10, 10);
            rt.localScale = Vector3.one * UIFactory.MetersPerPixel;

            instance = go.AddComponent<DragGhost>();
            instance.glyphRoot = UIFactory.Rect("Glyph", go.transform);
            instance.glyphRoot.anchorMin = instance.glyphRoot.anchorMax = new Vector2(0.5f, 0.5f);
            instance.glyphRoot.sizeDelta = Vector2.zero;
            instance.chip = UIFactory.Img(go.transform, "Chip", UIFactory.Orange, UIFactory.Rounded);
            instance.label = UIFactory.Text(instance.chip.transform, "", 22, UIFactory.TextLight, TextAlignmentOptions.Center, FontStyles.Bold);
            UIFactory.Stretch(instance.label.rectTransform, 10, 0, 10, 0);
            go.SetActive(false);
            return instance;
        }

        public static void Show(DragPayload p)
        {
            var g = Get();
            UIFactory.ClearChildren(g.glyphRoot);
            float ls = 20f;
            string chipText = null;
            switch (p.kind)
            {
                case DragKind.Note:
                case DragKind.Rest:
                case DragKind.MoveNote:
                    var n = p.kind == DragKind.MoveNote ? p.note.Clone() : new Note { duration = p.duration, dotted = p.dotted, isRest = p.kind == DragKind.Rest, step = 2 };
                    n.step = n.isRest ? 4 : 2;
                    n.accidental = 0; n.natural = false; n.staccato = n.accent = false; n.dynamic = 0;
                    var c = Engraver.Container(g.glyphRoot, "Held", new Vector2(0, n.isRest ? -2f * ls : 0));
                    c.anchorMin = c.anchorMax = new Vector2(0.5f, 0.5f);
                    Engraver.Note(c, n, ls, UIFactory.Orange, false);
                    break;
                case DragKind.Symbol:
                    var s = Engraver.Container(g.glyphRoot, "Held", Vector2.zero);
                    s.anchorMin = s.anchorMax = new Vector2(0.5f, 0.5f);
                    Engraver.SymbolIcon(s, p.symbol, p.symbol == SymbolKind.Dot || p.symbol == SymbolKind.Staccato ? 40f : ls, UIFactory.Orange);
                    break;
                case DragKind.Motif: chipText = BeginnerKit.Motifs[p.motif].name; break;
                case DragKind.Chord:
                case DragKind.MoveChord:
                    var app = VRComposerApp.Instance;
                    int deg = p.kind == DragKind.Chord ? p.chordDegree : p.chord.degree;
                    chipText = app != null ? BeginnerKit.ChordName(app.Score.Key, deg) : "Chord";
                    break;
            }
            g.chip.gameObject.SetActive(chipText != null);
            if (chipText != null)
            {
                g.label.text = chipText;
                var cr = g.chip.rectTransform;
                cr.anchorMin = cr.anchorMax = new Vector2(0.5f, 0.5f);
                cr.sizeDelta = new Vector2(Mathf.Max(70f, chipText.Length * 13f + 30f), 44f);
            }
            g.gameObject.SetActive(true);
        }

        public static void Move(Vector3 worldPos, Quaternion rotation)
        {
            var g = Get();
            // Canvas forward points away from the viewer: pull the ghost 1.5 cm towards them.
            g.transform.SetPositionAndRotation(worldPos - rotation * Vector3.forward * 0.015f, rotation);
            if (!g.gameObject.activeSelf) g.gameObject.SetActive(true);
        }

        public static void Hide()
        {
            if (instance != null) instance.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Everything that can be dragged (palette tiles, notes and chords on the score) uses this.
    /// Placement is computed from a smoothed pointer with sticky snapping, and on release the item
    /// lands where the ghost was shown just before the trigger was let go (not where the ray jumped).
    /// A tap without a drag calls onTap.
    /// </summary>
    public class DragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        public Func<DragPayload> payloadFactory;
        public Action onTap;
        public Action onBeginDrag, onEndDrag;

        private DragPayload payload;
        private readonly OneEuro2D filter = new OneEuro2D();
        private readonly StickySnap snap = new StickySnap();
        private Component currentZone;
        private float lastTime;
        private string lastKey;
        private PointerEventData lastEvent;

        private struct Pending
        {
            public float time;
            public Component zone;       // StaffZone, ChordLaneZone, BinZone or null
            public int tick, step, staffIndex, length;
            public Note target;          // symbol target
            public bool valid;
        }
        private readonly List<Pending> history = new List<Pending>();

        private static VRComposerApp App { get { return VRComposerApp.Instance; } }

        public void OnBeginDrag(PointerEventData e)
        {
            if (payloadFactory == null || App == null) return;
            payload = payloadFactory();
            if (payload == null) return;
            filter.Reset();
            snap.Reset();
            history.Clear();
            currentZone = null;
            lastKey = null;
            lastTime = Time.unscaledTime;
            DragGhost.Show(payload);
            if (onBeginDrag != null) onBeginDrag();
            UIFactory.Buzz(e, 0.35f, 0.04f);
            OnDrag(e);
        }

        public void OnDrag(PointerEventData e)
        {
            if (payload == null) return;
            lastEvent = e;
            var app = App;
            var view = app.View;
            var hit = e.pointerCurrentRaycast;
            float now = Time.unscaledTime;
            float dt = Mathf.Max(1e-3f, now - lastTime);
            lastTime = now;

            Component zone = null;
            if (hit.gameObject != null)
            {
                zone = Find<BinZone>(hit.gameObject);
                if (zone == null && payload.kind != DragKind.Chord && payload.kind != DragKind.MoveChord) zone = Find<StaffZone>(hit.gameObject);
                if (zone == null && (payload.kind == DragKind.Chord || payload.kind == DragKind.MoveChord)) zone = Find<ChordLaneZone>(hit.gameObject);
            }
            if (zone != currentZone) { filter.Reset(); snap.Reset(); currentZone = zone; }

            var p = new Pending { time = now, zone = zone };
            try
            {
            if (zone is StaffZone)
            {
                var sz = (StaffZone)zone;
                var rt = (RectTransform)sz.transform;
                Vector2 local = rt.InverseTransformPoint(hit.worldPosition);
                local = filter.Filter(local, dt);
                ComputeStaff(sz, local, ref p);
                DragGhost.Hide();
            }
            else if (zone is ChordLaneZone)
            {
                var rt = (RectTransform)zone.transform;
                Vector2 local = rt.InverseTransformPoint(hit.worldPosition);
                local = filter.Filter(local, dt);
                ComputeChord(view, local, ref p);
                DragGhost.Hide();
            }
            else
            {
                view.ClearOverlay();
                if (zone is BinZone) view.SetReadout("Release to delete");
                else view.SetReadout(payload.kind == DragKind.MoveNote || payload.kind == DragKind.MoveChord ? "Release here to delete" : "Drag onto the stave");
                lastKey = null;
                if (hit.gameObject != null) DragGhost.Move(hit.worldPosition, hit.gameObject.transform.rotation);
                else DragGhost.Hide();
            }
            }
            catch (Exception ex) { Debug.LogException(ex); }
            history.Add(p);
            if (history.Count > 90) history.RemoveRange(0, 30);
        }

        /// <summary>GetComponentInParent that returns a real null (the editor can return "fake null" objects, which pass `is` checks).</summary>
        private static Component Find<T>(GameObject g) where T : Component
        {
            var c = g.GetComponentInParent<T>();
            return c != null ? c : null;
        }

        private Func<int, bool> AllowedSteps(Staff staff)
        {
            var app = App;
            if (payload.IsRest) return s => s == 4;
            if (app.Score.IsBeginner) { var k = app.Score.Key; var clef = staff.clef; return s => BeginnerKit.InScale(k, clef, s); }
            return s => true;
        }

        private void ComputeStaff(StaffZone sz, Vector2 local, ref Pending p)
        {
            var app = App;
            var view = sz.view;
            var score = app.Score;
            p.staffIndex = sz.staffIndex;

            if (payload.kind == DragKind.Symbol)
            {
                Clef dropClef;
                if (Score.ClefOf(payload.symbol, out dropClef))
                {
                    p.valid = true;
                    string key = "clef" + sz.staffIndex;
                    if (key != lastKey) { lastKey = key; view.ClearOverlay(); view.SetReadout("Release to set the " + dropClef.ToString().ToLower() + " clef on " + sz.staff.name); }
                    return;
                }
                // nearest note under the pointer
                Note best = null; float bestD = float.MaxValue;
                foreach (var n in sz.staff.notes)
                {
                    if (!view.OnPage(n.tick)) continue;
                    float dx = Mathf.Abs(view.TickX(n.tick) - local.x);
                    float dy = n.isRest ? 0f : Mathf.Abs(view.StepY(n.step) - local.y) * 0.3f;
                    float d = dx + dy;
                    if (dx < Mathf.Max(view.SlotW * 1.2f, 1.6f * ScoreView.LS) && d < bestD) { bestD = d; best = n; }
                }
                p.target = best;
                p.valid = best != null;
                string k2 = best != null ? best.GetHashCode().ToString() : "none";
                if (k2 != lastKey)
                {
                    lastKey = k2;
                    view.ShowTargetNote(best);
                    view.SetReadout(best != null ? SymbolName(payload.symbol) + " on " + (best.isRest ? "rest" : app.Score.NameOf(sz.staff.clef, best.step, best.accidental, best.natural)) : "Point at a note to add " + SymbolName(payload.symbol).ToLower());
                }
                return;
            }

            int len = payload.Length16;
            int unit = view.Unit;
            int gridTicks = payload.kind == DragKind.Motif ? score.TicksPerBar : Durations.Grid(len);
            gridTicks = Mathf.Max(gridTicks, unit);
            int gridSlots = Mathf.Max(1, gridTicks / unit);

            float cont = view.ContinuousSlot(local.x) / gridSlots;
            if (payload.kind == DragKind.Motif) cont = Mathf.Floor(view.ContinuousSlot(local.x) / gridSlots + 0.0001f);
            int cell = snap.Cell(cont);
            int maxCell = view.BarsPerPage * view.SlotsPerBar / gridSlots - 1;
            cell = Mathf.Clamp(cell, 0, Mathf.Max(0, maxCell));
            int tick = view.SlotToTick(cell * gridSlots);
            // keep the note inside its bar (no ties)
            int tpb = score.TicksPerBar;
            int barStart = tick / tpb * tpb;
            if (tick + len > barStart + tpb) tick = Mathf.Max(barStart, barStart + tpb - len) / gridTicks * gridTicks;
            if (tick >= score.TotalTicks) tick = Mathf.Max(0, score.TotalTicks - len);

            float v = (local.y - view.BottomLineY) / (ScoreView.LS * 0.5f);
            int step = snap.Step(v, AllowedSteps(sz.staff));

            p.tick = tick;
            p.step = step;
            p.length = len;
            p.valid = !score.CrossesBar(tick, Mathf.Min(len, tpb));

            string keyStr = sz.staffIndex + "_" + tick + "_" + step;
            if (keyStr == lastKey) return;
            lastKey = keyStr;

            var notes = Realise(payload, sz.staff, tick, step);
            Fit fit = Fit.None;
            string why = null;
            if (score.IsBeginner) fit = BeginnerKit.RateNotes(score, sz.staff, notes, payload.kind == DragKind.MoveNote ? payload.note : null, p.valid, out why);
            view.ShowNoteGhost(sz.staffIndex, notes, p.valid, fit);
            int bar = tick / tpb + 1;
            float beat = (tick % tpb) / 4f + 1f;
            string what = payload.IsRest ? "Rest" : payload.kind == DragKind.Motif ? BeginnerKit.Motifs[payload.motif].name + " from " + app.Score.NameOf(sz.staff.clef, step)
                : app.Score.NameOf(sz.staff.clef, step, payload.kind == DragKind.MoveNote ? payload.note.accidental : 0);
            string fitText = fit == Fit.None ? "" : "   " + BeginnerKit.FitLabel(fit) + (string.IsNullOrEmpty(why) ? "" : ": " + why);
            view.SetReadout(what + "  ·  bar " + bar + ", beat " + beat.ToString(beat % 1f == 0f ? "0" : "0.##") + fitText);
            UIFactory.Buzz(lastEvent, 0.15f, 0.015f);
            if (!payload.IsRest && payload.kind != DragKind.Motif) app.Player.PreviewNote(sz.staff.program, app.Score.MidiOf(sz.staff.clef, step, payload.kind == DragKind.MoveNote ? payload.note.accidental : 0), 0.22f);
        }

        private void ComputeChord(ScoreView view, Vector2 local, ref Pending p)
        {
            var score = App.Score;
            int tpb = score.TicksPerBar;
            float rel = (local.x - view.X0) / view.BarW;
            int barInPage = Mathf.Clamp(Mathf.FloorToInt(rel), 0, view.BarsPerPage - 1);
            bool secondHalf = rel - barInPage >= 0.5f && tpb % 2 == 0;
            // sticky on half-bar cells
            int cell = snap.Cell(rel * 2f - 0.5f);
            cell = Mathf.Clamp(cell, 0, view.BarsPerPage * 2 - 1);
            barInPage = cell / 2;
            secondHalf = cell % 2 == 1 && tpb % 2 == 0;
            int bar = view.pageStart + barInPage;
            if (bar >= score.bars) { p.valid = false; return; }
            p.tick = bar * tpb + (secondHalf ? tpb / 2 : 0);
            p.length = secondHalf ? tpb / 2 : tpb;
            p.valid = true;
            int deg = payload.kind == DragKind.Chord ? payload.chordDegree : payload.chord.degree;
            string key = "c" + p.tick + "_" + p.length;
            if (key == lastKey) return;
            lastKey = key;
            Fit cfit = Fit.None;
            string cwhy = null;
            if (score.IsBeginner) cfit = BeginnerKit.RateChord(score, deg, p.tick, p.length, out cwhy);
            view.ShowChordGhost(p.tick, p.length, BeginnerKit.ChordName(score.Key, deg), BeginnerKit.ChordRoman(score.Key, deg), cfit);
            view.SetReadout(BeginnerKit.ChordName(score.Key, deg) + "  ·  bar " + (bar + 1) + (secondHalf ? ", second half" : "")
                + (cfit == Fit.None ? "" : "   " + BeginnerKit.FitLabel(cfit) + ": " + cwhy));
            UIFactory.Buzz(lastEvent, 0.15f, 0.015f);
        }

        private List<Note> Realise(DragPayload payload, Staff staff, int tick, int step)
        {
            var app = App;
            switch (payload.kind)
            {
                case DragKind.Motif:
                    return BeginnerKit.Realise(BeginnerKit.Motifs[payload.motif], app.Score.Key, staff.clef, tick, step, app.Score.TicksPerBar - tick % app.Score.TicksPerBar);
                case DragKind.MoveNote:
                    var m = payload.note.Clone();
                    m.tick = tick;
                    if (!m.isRest) m.step = step;
                    return new List<Note> { m };
                default:
                    return new List<Note> { new Note { tick = tick, duration = payload.duration, dotted = payload.dotted, isRest = payload.kind == DragKind.Rest, step = payload.kind == DragKind.Rest ? 4 : step } };
            }
        }

        public void OnEndDrag(PointerEventData e)
        {
            DragGhost.Hide();
            if (onEndDrag != null) onEndDrag();
            if (payload == null) return;
            var p = payload;
            payload = null;
            var app = App;
            app.View.ClearOverlay();
            app.View.RestoreReadout();

            Pending chosen;
            bool have = ChooseDrop(out chosen);
            history.Clear();
            Debug.Log("[VRComposer] Drop " + p.kind + ": " + (have ? (chosen.zone != null ? chosen.zone.GetType().Name : "outside") + (chosen.valid ? " tick " + chosen.tick + " step " + chosen.step : " (not placeable)") : "no position"));

            bool moving = p.kind == DragKind.MoveNote || p.kind == DragKind.MoveChord;
            if (!have || chosen.zone == null || chosen.zone is BinZone)
            {
                if (moving) app.DeleteDragged(p);
                return;
            }
            if (!chosen.valid) return;
            UIFactory.Buzz(e, 0.6f, 0.06f);

            if (chosen.zone is StaffZone)
            {
                var sz = (StaffZone)chosen.zone;
                if (p.kind == DragKind.Symbol) { app.ApplySymbol(sz.staff, chosen.target, p.symbol); return; }
                if (p.kind == DragKind.MoveNote) { app.MoveNote(p.fromStaff, p.note, sz.staff, chosen.tick, chosen.step); return; }
                app.DropOnStaff(sz.staff, Realise(p, sz.staff, chosen.tick, chosen.step), p);
            }
            else if (chosen.zone is ChordLaneZone)
            {
                if (p.kind == DragKind.MoveChord) app.MoveChord(p.chord, chosen.tick, chosen.length);
                else app.DropChord(p.chordDegree, chosen.tick, chosen.length);
            }
        }

        /// <summary>
        /// Where the item lands: the target the ghost showed. Releasing the trigger jerks the ray, so
        /// a target that changed in the last 80 ms is ignored, and if the ray slipped off the stave in
        /// the last 300 ms the drop still goes where the ghost was.
        /// </summary>
        private bool ChooseDrop(out Pending chosen)
        {
            chosen = default(Pending);
            if (history.Count == 0) return false;
            float now = Time.unscaledTime;
            var last = history[history.Count - 1];

            // most recent entry that was on a drop zone (and placeable)
            int lastOn = -1;
            for (int i = history.Count - 1; i >= 0; i--)
                if (history[i].zone != null && !(history[i].zone is BinZone) && history[i].valid) { lastOn = i; break; }

            if (last.zone == null || last.zone is BinZone)
            {
                // off the score now: count it as on the score if it only just slipped off
                if (lastOn >= 0 && last.zone == null)
                {
                    float leftAt = history[Mathf.Min(lastOn + 1, history.Count - 1)].time;
                    if (now - leftAt <= 0.3f) { chosen = history[lastOn]; return true; }
                }
                chosen = last;
                return true;
            }

            // on a zone: undo a last-instant jump caused by the trigger pull
            chosen = last;
            for (int i = history.Count - 2; i >= 0; i--)
            {
                var h = history[i];
                if (h.zone != last.zone || !h.valid) break;
                if (Same(h, last)) continue;
                if (now - history[i + 1].time <= 0.08f) chosen = h;
                break;
            }
            if (!chosen.valid && lastOn >= 0 && now - history[lastOn].time <= 0.3f) chosen = history[lastOn];
            return true;
        }

        private static bool Same(Pending a, Pending b)
        {
            return a.zone == b.zone && a.tick == b.tick && a.step == b.step && a.length == b.length && a.target == b.target && a.staffIndex == b.staffIndex;
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.dragging) return;
            UIFactory.Buzz(e, 0.3f, 0.03f);
            if (onTap != null) onTap();
        }

        public static string SymbolName(SymbolKind k)
        {
            switch (k)
            {
                case SymbolKind.Tie: return "Tie";
                case SymbolKind.Fermata: return "Fermata";
                case SymbolKind.Pianissimo: return "pp (v. soft)";
                case SymbolKind.Piano: return "p (soft)";
                case SymbolKind.Forte: return "f (loud)";
                case SymbolKind.Fortissimo: return "ff (v. loud)";
                case SymbolKind.Sforzando: return "sf (accent)";
                case SymbolKind.Crescendo: return "Cresc.";
                case SymbolKind.TrebleClef: return "Treble clef";
                case SymbolKind.AltoClef: return "Alto clef";
                case SymbolKind.BassClef: return "Bass clef";
                default: return k.ToString();
            }
        }
    }

    /// <summary>A note on the stave: hover shows its name, tap selects (thumbstick nudges), drag moves, drop off deletes.</summary>
    public class NoteHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public StaffZone zone;
        public Note note;

        private void Start()
        {
            var src = gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = DragKind.MoveNote, note = note, fromStaff = zone.staff, duration = note.duration, dotted = note.dotted };
            src.onTap = () => VRComposerApp.Instance.SelectNote(zone.staff, note, true);
            src.onBeginDrag = () => { var go = transform.parent.gameObject; foreach (var g in go.GetComponentsInChildren<Graphic>()) if (!g.raycastTarget) g.canvasRenderer.SetAlpha(0.25f); };
            src.onEndDrag = () => { if (this == null) return; var go = transform.parent.gameObject; foreach (var g in go.GetComponentsInChildren<Graphic>()) g.canvasRenderer.SetAlpha(1f); };
        }

        public void OnPointerEnter(PointerEventData e)
        {
            string text = note.isRest ? Durations.Name(note.duration, true) + "  ·  drag to move, tap to select"
                : VRComposerApp.Instance.Score.NameOf(zone.staff.clef, note.step, note.accidental, note.natural) + " " + Durations.Name(note.duration, false).ToLower() + "  ·  drag to move, tap to select";
            zone.view.SetHover(note, text);
            UIFactory.Buzz(e, 0.08f, 0.01f);
        }

        public void OnPointerExit(PointerEventData e) { zone.view.SetHover(null, null); }
    }

    /// <summary>A chord block in the lane: drag to move, drop off the lane to delete, tap to hear it.</summary>
    public class ChordHandle : MonoBehaviour
    {
        public ChordBlock chord;

        private void Start()
        {
            var src = gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = DragKind.MoveChord, chord = chord };
            src.onTap = () => VRComposerApp.Instance.PreviewChord(chord.degree);
            gameObject.AddComponent<HoverFeedback>().scale = 1.03f;
        }
    }
}
