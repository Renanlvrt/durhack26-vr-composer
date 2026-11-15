using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace VRComposer
{
    /// <summary>
    /// Quick note entry, so you never have to reach for the left panel:
    /// - A note value is always LOADED (1/8 to start). Point the right ray at the stave: its ghost
    ///   follows. Pull the trigger: it is placed there, and stays loaded for the next one. Tapping a
    ///   note tile on the palette, or picking on the note wheel, loads another value.
    /// - B = LOCK: the ghost freezes on the stave so you can lower your arm. Right stick up/down =
    ///   pitch, left/right = beat. Trigger places it and the ghost moves on to the next beat. B unlocks.
    /// - Hold X = NOTE WHEEL on the left wrist: twist the wrist (a big turn per slice, it loops round),
    ///   the yellow slice is the choice, release X to take it. "Symbols" opens a second ring.
    /// - Hold A = INSTRUMENT WHEEL on the right wrist: your instruments plus "+". Release on one to
    ///   write on it. "+" opens the families, then the instruments of that family.
    /// - Y = close a wheel / deselect. Play / Stop stays on the panels.
    /// Desktop: hold N / I for the wheels (arrows or mouse wheel step), L = lock, Enter = place.
    /// </summary>
    public class QuickEntry : MonoBehaviour
    {
        public static QuickEntry Instance { get; private set; }

        [Tooltip("Most degrees of wrist twist per wheel slice (the wheel uses less when it has many slices, so all of them stay in reach).")]
        public float twistStepDegrees = 32f;
        [Tooltip("Comfortable wrist roll each way from where you start (degrees). Every slice is reachable within it.")]
        public float comfortableTwistDegrees = 75f;
        [Tooltip("Smoothing of the wrist angle (seconds). Higher = steadier, slower.")]
        public float twistSmoothing = 0.04f;
        [Tooltip("Flip if twisting right moves the wheel the wrong way.")]
        public bool invertTwist = false;

        // ---------------- loaded note ----------------
        public Duration duration = Duration.Eighth;
        public bool rest, dotted;
        private readonly Note style = new Note();     // one-shot symbols for the next note placed
        private bool HasStyle { get { return style.accidental != 0 || style.natural || style.staccato || style.accent || style.tie; } }

        private VRComposerApp app;
        private DragSource hover;
        private StaffZone hoverZone;

        // ---------------- lock ----------------
        public bool Locked { get; private set; }
        private int lockStaff, lockTick, lockStep;
        private bool lockDirty;
        private Vector2Int heldDir;
        private float nextRepeat;

        // ---------------- wheels ----------------
        private Wheel noteWheel, instWheel, active;
        private enum Page { Notes, Symbols, Instruments, Families, FamilyInstruments }
        private Page page;
        private string family;
        private bool held;                 // the wheel's button is down (twisting)
        private float awaitingUntil;       // a sub-ring stays open this long waiting for the next hold
        // wrist dial: absolute mapping from the twist since the button was pressed
        private Quaternion dialOpen = Quaternion.identity;
        private int dialStart, dialOffset;
        private float dialRawPrev, dialUnwrapped, dialFiltered;
        private bool dialArmed;
        private float stickRepeatAt;
        private float lastStep;
        private int stickDirWas;

        private InputAction xBtn, aBtn, bBtn, trigger, rStick, lStick, scroll;

        public bool WheelOpen { get { return active != null; } }
        public bool UsesSticks { get { return Locked || WheelOpen; } }

        private void Awake()
        {
            Instance = this;
            app = GetComponent<VRComposerApp>();
            var go = new GameObject("Quick entry hover");
            go.transform.SetParent(transform, false);
            hover = go.AddComponent<DragSource>();   // never receives UI events: only used for its snapping and ghost
        }

        private void Start()
        {
            xBtn = Button("Note wheel", "<XRController>{LeftHand}/{PrimaryButton}", "<Keyboard>/n");
            aBtn = Button("Instrument wheel", "<XRController>{RightHand}/{PrimaryButton}", "<Keyboard>/i");
            bBtn = Button("Lock", "<XRController>{RightHand}/{SecondaryButton}", "<Keyboard>/l");
            trigger = Button("Place locked", "<XRController>{RightHand}/{TriggerButton}", "<Keyboard>/enter");
            bBtn.performed += _ => ToggleLock();
            trigger.performed += _ => { if (Locked && !WheelOpen) PlaceLocked(); };

            rStick = new InputAction("QE right stick", InputActionType.Value, expectedControlType: "Vector2");
            rStick.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
            rStick.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            rStick.Enable();
            lStick = new InputAction("QE left stick", InputActionType.Value, expectedControlType: "Vector2");
            lStick.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
            lStick.Enable();
            scroll = new InputAction("QE scroll", InputActionType.Value, expectedControlType: "Vector2");
            scroll.AddBinding("<Mouse>/scroll");
            scroll.Enable();

            noteWheel = new Wheel("Note wheel", transform);
            instWheel = new Wheel("Instrument wheel", transform);
            app.Score.Changed += () => { lockDirty = true; if (hover != null) hover.InvalidateHover(); };
        }

        private static InputAction Button(string name, params string[] bindings)
        {
            var a = new InputAction("QE " + name, InputActionType.Button);
            foreach (var b in bindings) a.AddBinding(b);
            a.Enable();
            return a;
        }

        private void OnDestroy()
        {
            foreach (var a in new[] { xBtn, aBtn, bBtn, trigger, rStick, lStick, scroll }) a?.Dispose();
        }

        // ======================================================== loaded note

        public bool IsLoaded(Duration d, bool isRest, bool isDotted) { return d == duration && isRest == rest && isDotted == dotted; }

        public string LoadedName
        {
            get
            {
                string n = Durations.Name(duration, rest);
                if (dotted) n = "Dotted " + n.ToLower();
                var extra = new List<string>();
                if (style.accidental > 0) extra.Add("sharp");
                if (style.accidental < 0) extra.Add("flat");
                if (style.natural) extra.Add("natural");
                if (style.staccato) extra.Add("staccato");
                if (style.accent) extra.Add("accent");
                if (style.tie) extra.Add("tie");
                return extra.Count > 0 ? n + " + " + string.Join(", ", extra) : n;
            }
        }

        public void Load(Duration d, bool isRest, bool isDotted)
        {
            duration = d; rest = isRest; dotted = isDotted;
            OnModeChanged();
            app.View.SetReadout("Loaded: " + LoadedName + "  ·  point at the stave and pull the trigger");
            Reloaded();
        }

        /// <summary>Beginner has no 1/16, dots or symbols: keep the loaded value valid.</summary>
        public void OnModeChanged()
        {
            if (app.Score.IsBeginner)
            {
                if (duration == Duration.Sixteenth) duration = Duration.Eighth;
                dotted = false;
                ClearStyle();
            }
            if (dotted && duration == Duration.Sixteenth) dotted = false;
        }

        private void Reloaded()
        {
            if (hover.Hovering) { hover.BeginHover(Payload()); }
            lockDirty = true;
            paletteDirty = true;   // rebuilt in LateUpdate, once no drag is running
        }

        private bool paletteDirty;

        private void ClearStyle() { style.accidental = 0; style.natural = style.staccato = style.accent = style.tie = false; }

        private DragPayload Payload()
        {
            return new DragPayload
            {
                kind = rest ? DragKind.Rest : DragKind.Note, duration = duration, dotted = dotted,
                style = HasStyle ? style.Clone() : null,
            };
        }

        private int Length16 { get { return dotted ? (int)duration * 3 / 2 : (int)duration; } }

        // ======================================================== point + trigger

        public void HoverStave(StaffZone zone, PointerEventData e)
        {
            if (Locked || WheelOpen || DragSource.Dragging) return;
            if (!hover.Hovering) hover.BeginHover(Payload());
            hoverZone = zone;
            hover.HoverMove(e);
        }

        public void LeaveStave(StaffZone zone, PointerEventData e)
        {
            if (zone != hoverZone) return;
            hoverZone = null;
            hover.EndHover();
        }

        /// <summary>A click / trigger on the stave: place the loaded note where the ghost is.</summary>
        public bool ClickStave(StaffZone zone, PointerEventData e)
        {
            if (Locked || WheelOpen) return true;            // the trigger belongs to the lock / wheel
            if (!hover.Hovering) { hover.BeginHover(Payload()); hover.HoverMove(e); }
            bool placed = hover.CommitHover(e);
            if (placed && HasStyle) { ClearStyle(); hover.BeginHover(Payload()); }
            return true;
        }

        // ======================================================== B lock

        private void ToggleLock()
        {
            if (WheelOpen) return;
            if (Locked) { Unlock(); return; }
            var score = app.Score;
            if (score.staves.Count == 0) return;
            int si, tick, step;
            if (!hover.LastHover(out si, out tick, out step))
            {
                // not pointing at the stave: start after the last note of the selected instrument
                si = Mathf.Clamp(app.SelectedStaff, 0, score.staves.Count - 1);
                var st = score.staves[si];
                tick = 0; step = 4;
                foreach (var n in st.notes) { if (n.End > tick) tick = n.End; if (!n.isRest) step = n.step; }
                tick = FitInBar(tick, Length16, 1);
                if (tick + Length16 > score.TotalTicks) tick = Mathf.Max(0, score.TotalTicks - Length16);
                if (score.IsBeginner) step = BeginnerKit.SnapToScale(score.Key, st.clef, step);
            }
            hover.EndHover();
            hoverZone = null;
            lockStaff = si; lockTick = tick; lockStep = step;
            Locked = true;
            app.Deselect();
            app.SelectStaff(si);
            app.RefreshLocomotion();
            lockDirty = true;
            Buzz(true, 0.4f, 0.05f);
        }

        private void Unlock()
        {
            Locked = false;
            app.View.ClearOverlay();
            app.View.RestoreReadout();
            app.RefreshLocomotion();
            Buzz(true, 0.25f, 0.04f);
        }

        /// <summary>Keeps a note inside one bar: if it would cross the barline, move to the next bar
        /// (going right) or to the last place it fits in this bar (going left).</summary>
        private int FitInBar(int tick, int len, int dir)
        {
            int tpb = app.Score.TicksPerBar;
            if (len >= tpb) return tick / tpb * tpb;
            if (tick / tpb == (tick + len - 1) / tpb) return tick;
            return dir > 0 ? (tick / tpb + 1) * tpb : tick / tpb * tpb + tpb - len;
        }

        private void HandleLockStick()
        {
            Vector2 v = rStick.ReadValue<Vector2>();
            Vector2Int dir = Vector2Int.zero;
            if (Mathf.Abs(v.y) > 0.7f && Mathf.Abs(v.y) >= Mathf.Abs(v.x)) dir.y = v.y > 0 ? 1 : -1;
            else if (Mathf.Abs(v.x) > 0.7f) dir.x = v.x > 0 ? 1 : -1;
            if (dir == Vector2Int.zero) { heldDir = dir; return; }
            float now = Time.unscaledTime;
            if (dir == heldDir && now < nextRepeat) return;
            nextRepeat = now + (dir == heldDir ? 0.12f : 0.35f);
            heldDir = dir;

            var score = app.Score;
            var staff = score.staves[lockStaff];
            if (dir.y != 0 && !rest)
            {
                int s = lockStep;
                do { s += dir.y; } while (s >= Score.MinStep && s <= Score.MaxStep && score.IsBeginner && !BeginnerKit.InScale(score.Key, staff.clef, s));
                if (s >= Score.MinStep && s <= Score.MaxStep) lockStep = s;
                app.Player.PreviewNote(staff.program, score.MidiOf(staff.clef, lockStep, style.accidental, style.natural), 0.25f);
            }
            if (dir.x != 0)
            {
                int len = Length16;
                int grid = Mathf.Max(Durations.Grid(len), app.View.Unit);
                int t = FitInBar(Mathf.Max(0, lockTick + dir.x * grid), len, dir.x);
                if (t >= 0 && t + len <= score.TotalTicks) lockTick = t;
            }
            Buzz(false, 0.12f, 0.015f);
            lockDirty = true;
        }

        private void PlaceLocked()
        {
            var score = app.Score;
            if (lockStaff >= score.staves.Count) { Unlock(); return; }
            var staff = score.staves[lockStaff];
            int len = Length16;
            var n = new Note { tick = lockTick, duration = duration, dotted = dotted, isRest = rest, step = rest ? 4 : lockStep };
            if (!rest && HasStyle)
            {
                n.accidental = style.accidental; n.natural = style.natural; n.staccato = style.staccato; n.accent = style.accent; n.tie = style.tie;
            }
            if (score.CrossesBar(n.tick, Mathf.Min(len, score.TicksPerBar))) { Buzz(false, 0.8f, 0.1f); return; }
            app.DropOnStaff(staff, new List<Note> { n }, Payload());
            app.Deselect();                 // the stick keeps moving the ghost, not the note just placed
            ClearStyle();
            Buzz(false, 0.6f, 0.05f);
            // move on to the next beat (add a bar at the end of the score if needed)
            int next = FitInBar(lockTick + len, len, 1);
            if (next + len > score.TotalTicks && score.bars < Score.MaxBars) { score.bars++; score.NotifyChanged(); }
            if (next + len <= score.TotalTicks) lockTick = next;
            lockDirty = true;
        }

        private void DrawLock()
        {
            lockDirty = false;
            var score = app.Score;
            var view = app.View;
            if (lockStaff >= score.staves.Count) { Unlock(); return; }
            if (app.SelectedStaff != lockStaff && app.SelectedStaff < score.staves.Count) lockStaff = app.SelectedStaff;   // instrument wheel moved us
            if (!view.OnPage(lockTick)) view.ShowPageOf(lockTick);
            var staff = score.staves[lockStaff];
            var n = new Note { tick = lockTick, duration = duration, dotted = dotted, isRest = rest, step = rest ? 4 : lockStep, accidental = style.accidental, natural = style.natural };
            var notes = new List<Note> { n };
            bool valid = !score.CrossesBar(n.tick, Mathf.Min(Length16, score.TicksPerBar));
            Fit fit = Fit.None;
            string why = null;
            if (score.IsBeginner) fit = BeginnerKit.RateNotes(score, staff, notes, null, valid, out why);
            view.ShowNoteGhost(lockStaff, notes, valid, fit);
            int tpb = score.TicksPerBar;
            float beat = (lockTick % tpb) / 4f + 1f;
            string what = rest ? "Rest" : score.NameOf(staff.clef, lockStep, style.accidental, style.natural);
            view.SetReadout("LOCKED  ·  " + what + " " + LoadedName.ToLower() + "  ·  bar " + (lockTick / tpb + 1) + ", beat " + beat.ToString(beat % 1f == 0f ? "0" : "0.##")
                + "  ·  stick moves, trigger places, B unlocks" + (fit == Fit.None ? "" : "  ·  " + BeginnerKit.FitLabel(fit)));
        }

        // ======================================================== wheels

        private struct Item
        {
            public string label;
            public Action<RectTransform> art;
            public bool current;
            public Func<bool> pick;     // true = stay open (a sub-ring)
        }

        private List<Item> Items(Page p)
        {
            var list = new List<Item>();
            var score = app.Score;
            switch (p)
            {
                case Page.Notes:
                {
                    var values = score.IsBeginner
                        ? new[] { Duration.Whole, Duration.Half, Duration.Quarter, Duration.Eighth }
                        : new[] { Duration.Whole, Duration.Half, Duration.Quarter, Duration.Eighth, Duration.Sixteenth };
                    string[] names = { "Whole", "Half", "1/4", "1/8", "1/16" };
                    for (int i = 0; i < values.Length; i++)
                    {
                        var d = values[i];
                        list.Add(new Item
                        {
                            label = names[i], current = d == duration && !rest,
                            art = r => NoteArt(r, d, dotted && d != Duration.Sixteenth),
                            pick = () => { Load(d, false, dotted && d != Duration.Sixteenth); return false; },
                        });
                    }
                    if (!score.IsBeginner)
                    {
                        list.Add(new Item
                        {
                            label = dotted ? "Dot: on" : "Dot: off", current = dotted,
                            art = r => Engraver.GlyphCentred(r, "augmentationDot", 0, 0, 30f, UIFactory.Ink),
                            pick = () => { if (duration != Duration.Sixteenth) Load(duration, rest, !dotted); return false; },
                        });
                        list.Add(new Item
                        {
                            label = "Symbols", art = r => Engraver.GlyphCentred(r, "accidentalSharp", 0, 0, 11f, UIFactory.Ink),
                            pick = () => { page = Page.Symbols; return true; },
                        });
                    }
                    break;
                }
                case Page.Symbols:
                {
                    var kinds = new[] { SymbolKind.Sharp, SymbolKind.Flat, SymbolKind.Natural, SymbolKind.Staccato, SymbolKind.Accent, SymbolKind.Tie };
                    string[] names = { "Sharp", "Flat", "Natural", "Staccato", "Accent", "Tie" };
                    for (int i = 0; i < kinds.Length; i++)
                    {
                        var k = kinds[i];
                        list.Add(new Item
                        {
                            label = names[i], current = StyleHas(k),
                            art = r => Engraver.SymbolIcon(r, k, k == SymbolKind.Staccato ? 26f : 11f, UIFactory.Ink),
                            pick = () => { ApplyWheelSymbol(k); return false; },
                        });
                    }
                    list.Add(new Item { label = "Clear", pick = () => { ClearStyle(); app.View.SetReadout("Next note: " + LoadedName); Reloaded(); return false; } });
                    break;
                }
                case Page.Instruments:
                {
                    for (int i = 0; i < score.staves.Count; i++)
                    {
                        int si = i;
                        var st = score.staves[i];
                        list.Add(new Item
                        {
                            label = st.name, current = i == app.SelectedStaff,
                            pick = () =>
                            {
                                app.SelectStaff(si);
                                app.Player.PreviewNote(st.program, score.MidiOf(st.clef, 4));
                                app.View.SetReadout("Writing on " + st.name);
                                lockDirty = true;
                                return false;
                            },
                        });
                    }
                    if (score.staves.Count < Score.MaxStaves)
                        list.Add(new Item { label = "+  Add", pick = () => { page = Page.Families; return true; } });
                    break;
                }
                case Page.Families:
                {
                    var seen = new List<string>();
                    foreach (var def in Instruments.All) if (!seen.Contains(def.group)) seen.Add(def.group);
                    foreach (var g in seen)
                    {
                        string fam = g;
                        list.Add(new Item
                        {
                            label = fam,
                            pick = () =>
                            {
                                var members = Members(fam);
                                if (members.Count == 1) { app.AddInstrument(members[0]); return false; }
                                family = fam; page = Page.FamilyInstruments; return true;
                            },
                        });
                    }
                    break;
                }
                case Page.FamilyInstruments:
                    foreach (var def in Members(family))
                    {
                        var dd = def;
                        list.Add(new Item { label = dd.name, pick = () => { app.AddInstrument(dd); return false; } });
                    }
                    break;
            }
            return list;
        }

        private static List<InstrumentDef> Members(string group)
        {
            var r = new List<InstrumentDef>();
            foreach (var d in Instruments.All) if (d.group == group) r.Add(d);
            return r;
        }

        private bool StyleHas(SymbolKind k)
        {
            switch (k)
            {
                case SymbolKind.Sharp: return style.accidental > 0;
                case SymbolKind.Flat: return style.accidental < 0;
                case SymbolKind.Natural: return style.natural;
                case SymbolKind.Staccato: return style.staccato;
                case SymbolKind.Accent: return style.accent;
                case SymbolKind.Tie: return style.tie;
                default: return false;
            }
        }

        /// <summary>On the selected note if there is one (you just placed it), otherwise on the next note.</summary>
        private void ApplyWheelSymbol(SymbolKind k)
        {
            if (app.SelectedNote != null && !app.SelectedNote.isRest && !Locked)
            {
                app.ApplySymbol(app.SelectedNoteStaff, app.SelectedNote, k);
                return;
            }
            switch (k)
            {
                case SymbolKind.Sharp: style.accidental = style.accidental > 0 ? 0 : 1; style.natural = false; break;
                case SymbolKind.Flat: style.accidental = style.accidental < 0 ? 0 : -1; style.natural = false; break;
                case SymbolKind.Natural: style.natural = !style.natural; style.accidental = 0; break;
                case SymbolKind.Staccato: style.staccato = !style.staccato; break;
                case SymbolKind.Accent: style.accent = !style.accent; break;
                case SymbolKind.Tie: style.tie = !style.tie; break;
            }
            app.View.SetReadout("Next note: " + LoadedName);
            Reloaded();
        }

        private static void NoteArt(RectTransform r, Duration d, bool isDotted)
        {
            const float ls = 9f;
            var holder = UIFactory.Rect("Note", r);
            holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
            holder.anchoredPosition = new Vector2(isDotted ? -4 : 0, d == Duration.Whole ? 0 : -1.4f * ls);
            Engraver.Note(holder, new Note { duration = d, step = 2, dotted = isDotted }, ls, UIFactory.Ink, false);
        }

        private void OpenWheel(Wheel w, Page p)
        {
            if (active != null && active != w) CloseWheel();
            hover.EndHover();
            hoverZone = null;
            if (Locked) app.View.ClearOverlay();
            page = p;
            active = w;
            ShowPage();
            if (w == noteWheel) app.SetWristMenuVisible(false);
            app.RefreshLocomotion();
        }

        private void ShowPage()
        {
            var items = Items(page);
            int start = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].current) { start = i; break; }
            string title;
            switch (page)
            {
                case Page.Notes: title = LoadedName; break;
                case Page.Symbols: title = "Symbols"; break;
                case Page.Instruments: title = app.CurrentStaff != null ? app.CurrentStaff.name : "Instruments"; break;
                case Page.Families: title = "Add: family"; break;
                default: title = family; break;
            }
            var labels = new List<string>();
            var arts = new List<Action<RectTransform>>();
            var cur = new List<bool>();
            foreach (var it in items) { labels.Add(it.label); arts.Add(it.art); cur.Add(it.current); }
            active.Show(labels, arts, cur, start, title);
        }

        private void CloseWheel()
        {
            if (active == null) return;
            active.Hide();
            if (active == noteWheel) app.SetWristMenuVisible(true);
            active = null;
            held = false;
            awaitingUntil = 0;
            app.RefreshLocomotion();
            if (Locked) lockDirty = true;
        }

        /// <summary>Y: closes an open wheel. Returns false if there was nothing to close.</summary>
        public bool Cancel()
        {
            if (!WheelOpen) return false;
            CloseWheel();
            app.View.RestoreReadout();
            return true;
        }

        private void HandleWheelButton(InputAction btn, Wheel w, Page first)
        {
            if (btn.WasPressedThisFrame())
            {
                if (active != w) OpenWheel(w, first);
                held = true;
                ArmDial(w);
                lastStep = Time.unscaledTime;
                Buzz(w == noteWheel, 0.3f, 0.04f);
            }
            if (active == w && held && btn.WasReleasedThisFrame())
            {
                held = false;
                var items = Items(page);
                int i = w.Highlight;
                bool stay = i >= 0 && i < items.Count && items[i].pick != null && items[i].pick();
                if (stay && active == w)
                {
                    ShowPage();
                    awaitingUntil = Time.unscaledTime + 6f;     // hold the button again to choose in the sub-ring
                    Buzz(w == noteWheel, 0.3f, 0.04f);
                }
                else CloseWheel();
            }
        }

        private Transform Hand(Wheel w)
        {
            if (DesktopActive) return null;
            // cached: GameObject.Find walks the whole scene and this runs every frame a wheel is open
            if (w == noteWheel) { if (leftHand == null || !leftHand.gameObject.activeInHierarchy) leftHand = VRComposerApp.FindLeftHand(); return leftHand; }
            if (rightHand == null || !rightHand.gameObject.activeInHierarchy) rightHand = VRComposerApp.FindRightHand();
            return rightHand;
        }

        private Transform leftHand, rightHand;

        /// <summary>The controller's pointing and up axes, read straight from the XR device (no scene
        /// object needed), else from the controller transform. False without a tracked controller.</summary>
        private void ArmDial(Wheel w)
        {
            Quaternion q;
            dialArmed = HandRotation(w, out q);
            dialOpen = dialArmed ? q : Quaternion.identity;
            dialStart = w.Highlight;
            dialOffset = 0;
            dialRawPrev = dialUnwrapped = dialFiltered = 0f;
            w.SetPointer(dialStart);
        }

        /// <summary>The controller rotation (tracking space from the XR device, else the controller object).</summary>
        private bool HandRotation(Wheel w, out Quaternion q)
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(w == noteWheel ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
            bool isTracked;
            if (dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out q)
                && (!dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out isTracked) || isTracked))
                return true;
            var h = Hand(w);
            if (h != null) { q = h.rotation; return true; }
            q = Quaternion.identity;
            return false;
        }

        private bool HandPose(Wheel w, out Vector3 fwd, out Vector3 up)
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(w == noteWheel ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
            Quaternion q;
            bool isTracked;
            if (dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out q)
                && (!dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out isTracked) || isTracked))
            {
                fwd = q * Vector3.forward; up = q * Vector3.up;
                return true;
            }
            var h = Hand(w);
            if (h != null) { fwd = h.forward; up = h.up; return true; }
            fwd = Vector3.forward; up = Vector3.up;
            return false;
        }

        /// <summary>Controller position in the world from the XR device: tracking space is the camera's parent (Camera Offset).</summary>
        private static bool DeviceWorldPosition(bool left, Transform head, out Vector3 pos)
        {
            pos = Vector3.zero;
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(left ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
            Vector3 p;
            if (!dev.isValid || !dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out p)) return false;
            pos = head.parent != null ? head.parent.TransformPoint(p) : p;
            return true;
        }

        private static bool DesktopActive
        {
            get { var d = VRComposerApp.Instance != null ? VRComposerApp.Instance.GetComponent<DesktopMode>() : null; return d != null && d.Active; }
        }

        private void HandleTwist()
        {
            var w = active;
            int step = 0;
            Quaternion q;
            bool tracked = HandRotation(w, out q);
            if (held && tracked)
            {
                if (!dialArmed) ArmDial(w);
                // Wrist dial (like turning a knob):
                //  - only the twist about the controller's own pointing axis counts (swing-twist
                //    decomposition of the rotation since the press), so raising, lowering or swinging
                //    the controller does not move the wheel;
                //  - absolute: the same wrist angle is always the same slice, so going back is exact;
                //  - slices get narrower on a busy wheel so all of them fit in a comfortable roll;
                //  - smoothed, with hysteresis at the borders so it never flickers between two slices.
                var rel = Quaternion.Inverse(dialOpen) * q;
                if (rel.w < 0f) { rel.x = -rel.x; rel.y = -rel.y; rel.z = -rel.z; rel.w = -rel.w; }
                float raw = 2f * Mathf.Atan2(rel.z, rel.w) * Mathf.Rad2Deg;          // twist about local +Z
                dialUnwrapped += Mathf.DeltaAngle(dialRawPrev, raw);
                dialRawPrev = raw;
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.001f, twistSmoothing));
                dialFiltered += (dialUnwrapped - dialFiltered) * k;

                int n = Mathf.Max(1, w.Count);
                int half = Mathf.Max(1, n / 2);
                float perSlice = Mathf.Clamp(comfortableTwistDegrees / half, 16f, twistStepDegrees);
                float sign = invertTwist ? 1f : -1f;     // a twist to the right (clockwise as you see it) moves clockwise
                float cont = sign * dialFiltered / perSlice;
                if (Mathf.Abs(cont - dialOffset) > 0.62f)
                {
                    int next = Mathf.RoundToInt(cont);
                    step = next - dialOffset;
                    dialOffset = next;
                }
                w.SetPointer(dialStart + cont);
                if (step != 0)
                {
                    w.SetHighlight(dialStart + dialOffset);
                    lastStep = Time.unscaledTime;
                    Buzz(w == noteWheel, 0.22f, 0.018f);     // one detent per slice
                    step = 0;                                 // already applied
                }
            }
            // In VR the wheel turns with the wrist twist only. Without a tracked controller (desktop
            // testing) the arrow keys step instead: a press moves one slice, hold to keep stepping.
            if (!tracked)
            {
                Vector2 s = (w == noteWheel ? lStick : rStick).ReadValue<Vector2>();
                int sd = s.x > 0.6f ? 1 : s.x < -0.6f ? -1 : 0;
                if (sd != 0 && Mathf.Abs(s.x) < Mathf.Abs(s.y) * 0.8f) sd = stickDirWas == sd ? sd : 0;   // mostly up/down: ignore
                float now = Time.unscaledTime;
                if (sd != 0 && sd != stickDirWas) { step = sd; stickRepeatAt = now + 0.45f; }
                else if (sd != 0 && now >= stickRepeatAt) { step = sd; stickRepeatAt = now + 0.22f; }
                stickDirWas = sd;
            }
            float sc = scroll.ReadValue<Vector2>().y;
            if (Mathf.Abs(sc) > 0.1f) step = sc < 0 ? 1 : -1;

            if (step != 0)
            {
                w.Step(step);
                lastStep = Time.unscaledTime;
                Buzz(w == noteWheel, 0.18f, 0.02f);
            }
        }

        private static void Buzz(bool left, float amp, float dur)
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(left ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
            if (dev.isValid) dev.SendHapticImpulse(0, amp, dur);
        }

        // ======================================================== frame

        private float nextRayCheck;
        private bool leftRayReady;

        /// <summary>The left controller gets a working UI ray like the right one (point and pull the
        /// left trigger on the panels and the stave).</summary>
        private void EnsureLeftRay()
        {
            if (leftRayReady || Time.unscaledTime < nextRayCheck) return;
            nextRayCheck = Time.unscaledTime + 2f;
            var left = VRComposerApp.FindLeftHand();
            if (left == null) return;
            int n = 0;
            foreach (var nf in left.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor>(true))
            {
                nf.enableUIInteraction = true;
                nf.enableFarCasting = true;
                if (!nf.enabled) nf.enabled = true;
                n++;
            }
            foreach (var ray in left.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>(true))
            {
                if (ray.name.IndexOf("Teleport", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                ray.enableUIInteraction = true;
                n++;
            }
            if (n > 0) { leftRayReady = true; Debug.Log("[VRComposer] Left controller ray: UI on (" + n + " interactor(s))."); }
        }

        private void Update()
        {
            EnsureLeftRay();
            if (app == null || noteWheel == null) return;
            if (!DragSource.Dragging)
            {
                HandleWheelButton(xBtn, noteWheel, Page.Notes);
                HandleWheelButton(aBtn, instWheel, Page.Instruments);
            }
            if (active != null)
            {
                HandleTwist();
                if (!held && awaitingUntil > 0 && Time.unscaledTime > awaitingUntil) CloseWheel();
            }
            else if (Locked) HandleLockStick();
        }

        private void LateUpdate()
        {
            if (active != null)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 hp;
                    var h = Hand(active);
                    if (h != null && h.gameObject.activeInHierarchy) active.Follow(h.position, true, cam.transform, active == noteWheel);
                    else if (DeviceWorldPosition(active == noteWheel, cam.transform, out hp)) active.Follow(hp, true, cam.transform, active == noteWheel);
                    else active.Follow(Vector3.zero, false, cam.transform, active == noteWheel);
                }
            }
            if (Locked && lockDirty && active == null) DrawLock();
            if (paletteDirty && !DragSource.Dragging) { paletteDirty = false; app.RebuildPalette(); }
        }

        // ======================================================== wheel view

        /// <summary>A ring of slices on a small world-space canvas that follows a hand and faces you.</summary>
        private class Wheel
        {
            const float Size = 440f, ROut = 210f, RIn = 92f;
            private readonly Canvas canvas;
            private readonly RectTransform root, slicesRoot;
            private readonly TextMeshProUGUI title, hint;
            private readonly List<Image> slices = new List<Image>();
            private readonly List<TextMeshProUGUI> labels = new List<TextMeshProUGUI>();
            private readonly List<bool> current = new List<bool>();
            public int Highlight { get; private set; }
            public int Count { get { return slices.Count; } }
            private static Sprite ring, disc;
            private RectTransform pointer;

            static readonly Color Normal = new Color(1f, 1f, 1f, 0.62f), Yellow = new Color(1f, 0.84f, 0.15f, 0.88f), Current = new Color(1f, 0.93f, 0.85f, 0.72f);

            public Wheel(string name, Transform parent)
            {
                canvas = UIFactory.WorldCanvas(name, new Vector2(Size, Size), parent);
                canvas.transform.localScale = Vector3.one * 0.00036f;       // about 16 cm across
                canvas.sortingOrder = 50;
                foreach (var r in canvas.GetComponents<BaseRaycaster>()) r.enabled = false;   // the rays pass through
                root = (RectTransform)canvas.transform;
                var back = UIFactory.Img(root, "Glass", new Color(0.1f, 0.1f, 0.15f, 0.4f), Disc);
                Centre(back.rectTransform, Vector2.zero, new Vector2(Size, Size));
                slicesRoot = UIFactory.Rect("Slices", root);
                Centre(slicesRoot, Vector2.zero, new Vector2(Size, Size));
                // the needle: follows the wrist continuously, so you see the dial turning with you
                var needle = UIFactory.Img(root, "Wrist needle", UIFactory.Orange, Disc);
                pointer = needle.rectTransform;
                Centre(pointer, new Vector2(0, ROut - 16f), new Vector2(22f, 22f));
                var hub = UIFactory.Img(root, "Hub", new Color(0.97f, 0.96f, 0.93f, 0.75f), Disc);
                Centre(hub.rectTransform, Vector2.zero, new Vector2(RIn * 2f - 12f, RIn * 2f - 12f));
                title = UIFactory.Text(root, "", 22, UIFactory.Ink, TextAlignmentOptions.Center, FontStyles.Bold);
                title.textWrappingMode = TextWrappingModes.Normal;
                Centre(title.rectTransform, new Vector2(0, 10), new Vector2(RIn * 1.6f, 70));
                hint = UIFactory.Text(root, "", 15, new Color(0.3f, 0.3f, 0.36f, 1f), TextAlignmentOptions.Center);
                Centre(hint.rectTransform, new Vector2(0, -42), new Vector2(RIn * 1.7f, 24));
                canvas.gameObject.SetActive(false);
            }

            private static void Centre(RectTransform rt, Vector2 pos, Vector2 size)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = pos;
                rt.sizeDelta = size;
            }

            public void Show(List<string> names, List<Action<RectTransform>> arts, List<bool> cur, int start, string titleText)
            {
                UIFactory.ClearChildren(slicesRoot);
                slices.Clear(); labels.Clear(); current.Clear();
                int n = Mathf.Max(1, names.Count);
                float span = 360f / n;
                for (int i = 0; i < names.Count; i++)
                {
                    float a0 = i * span - span / 2f + 1.5f;
                    var img = UIFactory.Img(slicesRoot, "Slice " + names[i], Normal, Ring);
                    img.type = Image.Type.Filled;
                    img.fillMethod = Image.FillMethod.Radial360;
                    img.fillOrigin = (int)Image.Origin360.Top;
                    img.fillClockwise = true;
                    img.fillAmount = (span - 3f) / 360f;
                    Centre(img.rectTransform, Vector2.zero, new Vector2(ROut * 2f, ROut * 2f));
                    img.rectTransform.localRotation = Quaternion.Euler(0, 0, -a0);
                    slices.Add(img);

                    float a = i * span * Mathf.Deg2Rad, r = (RIn + ROut) * 0.5f;
                    var c = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r;
                    bool hasArt = arts[i] != null;
                    var label = UIFactory.Text(slicesRoot, names[i], hasArt ? 15 : 19, UIFactory.Ink, TextAlignmentOptions.Center, FontStyles.Bold);
                    label.textWrappingMode = TextWrappingModes.Normal;
                    Centre(label.rectTransform, c + new Vector2(0, hasArt ? -28 : 0), new Vector2(Mathf.Min(120f, 2f * Mathf.PI * r / n), 44));
                    labels.Add(label);
                    if (hasArt)
                    {
                        var art = UIFactory.Rect("Art", slicesRoot);
                        Centre(art, c + new Vector2(0, 10), Vector2.zero);
                        try { arts[i](art); } catch (Exception e) { Debug.LogWarning(e); }
                    }
                    current.Add(cur[i]);
                }
                title.text = titleText;
                Highlight = Mathf.Clamp(start, 0, names.Count - 1);
                Paint();
                SetPointer(Highlight);
                pointer.SetAsLastSibling();
                canvas.gameObject.SetActive(true);
            }

            public void SetHighlight(int index)
            {
                if (slices.Count == 0) return;
                int h = ((index % slices.Count) + slices.Count) % slices.Count;   // loops round
                if (h == Highlight) return;
                Highlight = h;
                Paint();
            }

            /// <summary>Needle position in slices (fractional): 0 = top slice centre, clockwise.</summary>
            public void SetPointer(float slicePos)
            {
                if (pointer == null || slices.Count == 0) return;
                float a = slicePos * (360f / slices.Count) * Mathf.Deg2Rad;
                float r = ROut - 16f;
                pointer.anchoredPosition = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r;
            }

            public void Step(int dir)
            {
                if (slices.Count == 0) return;
                Highlight = ((Highlight + dir) % slices.Count + slices.Count) % slices.Count;   // loops round
                Paint();
                SetPointer(Highlight);
            }

            private void Paint()
            {
                for (int i = 0; i < slices.Count; i++)
                {
                    slices[i].color = i == Highlight ? Yellow : current[i] ? Current : Normal;
                    labels[i].color = current[i] ? UIFactory.Orange : UIFactory.Ink;
                    float s = i == Highlight ? 1.06f : 1f;
                    slices[i].rectTransform.localScale = Vector3.one * s;
                }
                if (Highlight >= 0 && Highlight < labels.Count) hint.text = labels[Highlight].text + "  ·  release";
            }

            public void Hide() { canvas.gameObject.SetActive(false); }

            /// <summary>Above the hand (or at the side of the view in desktop mode), always facing you.
            /// The wheel follows the hand's position only, so it doesn't spin while you twist.</summary>
            public void Follow(Vector3 handPos, bool haveHand, Transform head, bool left)
            {
                Vector3 pos;
                if (haveHand) pos = handPos + Vector3.up * 0.09f + (head.position - handPos).normalized * 0.03f;
                else pos = head.TransformPoint(new Vector3(left ? -0.17f : 0.17f, -0.08f, 0.45f));
                var t = canvas.transform;
                t.position = pos;
                var look = pos - head.position;
                if (look.sqrMagnitude > 1e-5f) t.rotation = Quaternion.LookRotation(look, head.up);
            }

            private static Sprite Ring { get { if (ring == null) ring = MakeCircle(RIn / ROut); return ring; } }
            private static Sprite Disc { get { if (disc == null) disc = MakeCircle(0f); return disc; } }

            private static Sprite MakeCircle(float inner)
            {
                const int size = 256;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                float c = size / 2f, rOut = c - 1f, rIn = inner * rOut;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(c, c));
                        float a = Mathf.Clamp01(rOut - d + 0.5f) * (inner > 0f ? Mathf.Clamp01(d - rIn + 0.5f) : 1f);
                        px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                    }
                tex.SetPixels32(px);
                tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            }
        }
    }
}
