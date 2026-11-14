using System;
using System.Collections;
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
    /// VR composer for Quest. Put this on one GameObject in a scene with an XR Origin (SampleScene).
    /// Builds everything at runtime:
    ///   Welcome (pick a place + Beginner/Advanced) · Score (centre) · Palette (left) ·
    ///   Inspector (right) · Transport (under the score) · Wrist menu (left hand).
    /// Placed notes play exactly as written through the SoundFont; nothing is generated.
    /// </summary>
    [RequireComponent(typeof(ScorePlayer))]
    public class VRComposerApp : MonoBehaviour
    {
        public static VRComposerApp Instance { get; private set; }

        [Header("Layout (metres / degrees, relative to the head when recentred)")]
        [Tooltip("Figma: palette left, inspector right.")]
        public bool paletteOnLeft = true;
        public float scoreDistance = 1.05f;
        public float scoreDegreesBelowEye = 10f;
        public float sideDistance = 0.7f;
        public float sideYawDegrees = 56f;
        public float transportDistance = 0.75f;
        [Tooltip("Hide the VR template's tutorial props (Interactables, CoachingCardRoot).")]
        public bool hideTemplateProps = true;
        public bool showWelcome = true;
        public bool followPlayhead = true;

        [Header("Wrist menu")]
        public bool createWristMenu = true;
        [Tooltip("Optional. If empty, a GameObject named like 'Left Controller' / 'Left Hand' is searched.")]
        public Transform wristAnchor;
        public Vector3 wristLocalPosition = new Vector3(0f, 0.07f, -0.02f);
        public Vector3 wristLocalEuler = new Vector3(40f, 0f, 0f);

        // Scenes saved with an older version keep their old serialized distances (1.2 m / 0.8 m);
        // bump this to move them to the new, closer defaults once.
        private const int CurrentLayoutVersion = 2;
        [SerializeField, HideInInspector] private int layoutVersion;

        private void MigrateLayout()
        {
            if (layoutVersion >= CurrentLayoutVersion) return;
            scoreDistance = 1.05f;
            scoreDegreesBelowEye = 10f;
            sideDistance = 0.7f;
            sideYawDegrees = 56f;
            transportDistance = 0.75f;
            wristLocalPosition = new Vector3(0f, 0.07f, -0.02f);
            layoutVersion = CurrentLayoutVersion;
        }

        private void OnValidate() { MigrateLayout(); }

        public Score Score { get; private set; }
        public ScorePlayer Player { get; private set; }
        public ScoreView View { get; private set; }
        public ThemeManager Themes { get; private set; }
        public int SelectedStaff { get; private set; }
        public Note SelectedNote { get; private set; }
        public Staff SelectedNoteStaff { get; private set; }

        private Transform panelRoot;
        private Canvas scoreCanvas, paletteCanvas, inspectorCanvas, transportCanvas, wristCanvas, welcomeCanvas;
        private RectTransform paletteContent, inspectorContent, welcomeContent;
        private Button playButton, loopButton, undoButton, wristPlay, wristMode, wristHide, wristTheme;
        private TextMeshProUGUI positionText, paletteHint;
        private string paletteTab = "Notes";
        private bool panelsVisible = true, started;
        private Vector3 layoutHead, layoutFwd;
        private float lastScoreHeight;

        private InputAction nudgeAction, backAction, playAction;
        private Vector2Int heldDir;
        private float nextRepeat;
        private readonly List<Behaviour> pausedLocomotion = new List<Behaviour>();

        // ================================================================== lifecycle

        private void Awake()
        {
            Instance = this;
            MigrateLayout();
            EnsureEventSystem();

            Score = new Score();
            Score.AddStaff(Instruments.All[0]); // Flute

            Player = GetComponent<ScorePlayer>();
            Player.Bind(Score);
            Player.loop = false;

            panelRoot = new GameObject("VR Composer Panels").transform;
            panelRoot.SetParent(transform, false);

            BuildScore();
            BuildPalette();
            BuildInspector();
            BuildTransport();
            BuildWelcome();

            Themes = gameObject.AddComponent<ThemeManager>();
            Themes.Init(this);

            Score.Changed += OnScoreChanged;
            SetupInput();
            Refresh();
            ShowComposer(!showWelcome);
        }

        private IEnumerator Start()
        {
            // Head pose is not valid on the first frames in XR: wait a moment before placing panels.
            float t = 0f;
            while (t < 1.5f && (Camera.main == null || Camera.main.transform.position == Vector3.zero))
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return null;
            if (hideTemplateProps)
                foreach (var n in new[] { "Interactables", "CoachingCardRoot" })
                {
                    var go = GameObject.Find(n);
                    if (go != null) go.SetActive(false);
                }
            Recenter();
            if (createWristMenu) BuildWristMenu();
        }

        private void OnDestroy()
        {
            if (Score != null) Score.Changed -= OnScoreChanged;
            nudgeAction?.Dispose();
            backAction?.Dispose();
            playAction?.Dispose();
            ResumeLocomotion();
            if (Instance == this) Instance = null;
        }

        private static void EnsureEventSystem()
        {
            var es = FindAnyObjectByType<EventSystem>();
            if (es == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(XRUIInputModule));
                return;
            }
            if (es.GetComponent<XRUIInputModule>() == null)
                Debug.LogWarning("[VRComposer] The EventSystem has no XRUIInputModule: XR rays will not click the panels.");
        }

        private void ShowComposer(bool on)
        {
            started = on;
            welcomeCanvas.gameObject.SetActive(!on);
            scoreCanvas.gameObject.SetActive(on && panelsVisible);
            paletteCanvas.gameObject.SetActive(on && panelsVisible);
            inspectorCanvas.gameObject.SetActive(on && panelsVisible);
            transportCanvas.gameObject.SetActive(on && panelsVisible);
        }

        // ================================================================== layout

        public void Recenter()
        {
            var cam = Camera.main;
            if (cam == null) { Debug.LogWarning("[VRComposer] No MainCamera found."); return; }
            layoutHead = cam.transform.position;
            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            layoutFwd = fwd.normalized;
            Layout();
        }

        private void Layout()
        {
            if (scoreCanvas == null) return;
            Vector3 head = layoutHead, fwd = layoutFwd;
            float drop = Mathf.Tan(scoreDegreesBelowEye * Mathf.Deg2Rad) * scoreDistance;
            Vector3 scorePos = head + fwd * scoreDistance - Vector3.up * drop;
            Place(scoreCanvas, scorePos, head);
            Place(welcomeCanvas, head + fwd * 0.9f - Vector3.up * 0.1f, head);

            // transport: just below the score's bottom edge as seen from the head, closer to the user
            float scoreH = View.Height * UIFactory.MetersPerPixel;
            float bottomAngle = Mathf.Atan2(drop + scoreH * 0.5f, scoreDistance) * Mathf.Rad2Deg;
            float tH = 120f * 0.75f * UIFactory.MetersPerPixel;
            float tAngle = bottomAngle + 4f + Mathf.Atan2(tH * 0.5f, transportDistance) * Mathf.Rad2Deg;
            Vector3 tPos = head + fwd * transportDistance - Vector3.up * Mathf.Tan(tAngle * Mathf.Deg2Rad) * transportDistance;
            Place(transportCanvas, tPos, head);

            float side = paletteOnLeft ? -1f : 1f;
            Vector3 palDir = Quaternion.Euler(0, side * sideYawDegrees, 0) * fwd;
            Vector3 insDir = Quaternion.Euler(0, -side * sideYawDegrees, 0) * fwd;
            float sideDrop = Mathf.Tan(8f * Mathf.Deg2Rad) * sideDistance;
            Place(paletteCanvas, head + palDir * sideDistance - Vector3.up * sideDrop, head);
            Place(inspectorCanvas, head + insDir * sideDistance - Vector3.up * sideDrop, head);
            lastScoreHeight = View.Height;
        }

        private static void Place(Canvas c, Vector3 pos, Vector3 head)
        {
            if (c == null) return;
            Vector3 dir = pos - head;
            dir.y *= 0.6f;
            c.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized, Vector3.up));
        }

        public void TogglePanels()
        {
            panelsVisible = !panelsVisible;
            ShowComposer(started);
            if (!started) welcomeCanvas.gameObject.SetActive(panelsVisible);
            UIFactory.SetButton(wristHide, panelsVisible ? "Hide" : "Show", UIFactory.Dark);
        }

        // ================================================================== state changes

        private void OnScoreChanged()
        {
            if (SelectedNote != null && (SelectedNoteStaff == null || !Score.staves.Contains(SelectedNoteStaff) || !SelectedNoteStaff.notes.Contains(SelectedNote))) SetSelection(null, null);
            Refresh();
        }

        private void Refresh()
        {
            if (SelectedStaff >= Score.staves.Count) SelectedStaff = Mathf.Max(0, Score.staves.Count - 1);
            View.Rebuild();
            RebuildPalette();
            RebuildInspector();
            UpdateIdleReadout();
            if (Mathf.Abs(View.Height - lastScoreHeight) > 1f && layoutFwd != Vector3.zero) Layout();
        }

        private void UpdateIdleReadout()
        {
            string s;
            if (SelectedNote != null)
                s = "Selected " + (SelectedNote.isRest ? "rest" : Score.StepToName(SelectedNoteStaff.clef, SelectedNote.step, SelectedNote.accidental)) + "  ·  thumbstick to nudge, B to finish";
            else if (Score.staves.TrueForAll(st => st.notes.Count == 0))
                s = Score.IsBeginner ? "Drag a motif or a note from the palette onto the stave" : "Drag a note from the palette onto the stave";
            else s = "Tap a note to select it  ·  press Play to hear your piece";
            View.SetIdleReadout(s);
        }

        public void OnThemeChanged(VenueId id, int venueProgram)
        {
            // In Beginner mode the venue's band plays your melody (strings, sax, brass).
            if (Score.IsBeginner && venueProgram >= 0 && Score.staves.Count > 0)
            {
                var s = Score.staves[0];
                s.program = venueProgram;
                s.name = Instruments.All[Instruments.IndexOfProgram(venueProgram)].program == venueProgram ? Instruments.All[Instruments.IndexOfProgram(venueProgram)].name : (venueProgram == 66 ? "Tenor Sax" : "Melody");
                Score.NotifyChanged();
            }
            else Refresh();
            UIFactory.SetButton(wristTheme, ThemeManager.Title(id), UIFactory.Indigo);
        }

        public void SetMode(ComposeMode m)
        {
            paletteTab = "Notes";
            View.pageStart = 0;
            Deselect();
            Score.SetMode(m);
            UIFactory.SetButton(wristMode, m == ComposeMode.Beginner ? "Beginner" : "Advanced", UIFactory.Indigo);
        }

        public void SelectStaff(int index)
        {
            SelectedStaff = Mathf.Clamp(index, 0, Mathf.Max(0, Score.staves.Count - 1));
            Refresh();
        }

        private Staff CurrentStaff
        {
            get { return Score.staves.Count == 0 ? null : Score.staves[Mathf.Clamp(SelectedStaff, 0, Score.staves.Count - 1)]; }
        }

        // ================================================================== selection + thumbstick nudge

        public void SelectNote(Staff s, Note n, bool preview)
        {
            if (SelectedNote == n) { Deselect(); return; }
            SetSelection(s, n);
            if (s != null) SelectedStaff = Score.staves.IndexOf(s);
            if (preview && n != null && !n.isRest) Player.PreviewNote(s.program, Score.StepToMidi(s.clef, n.step, n.accidental));
            View.ApplySelectionVisual();
            UpdateIdleReadout();
        }

        public void Deselect()
        {
            if (SelectedNote == null) return;
            SetSelection(null, null);
            View.ApplySelectionVisual();
            UpdateIdleReadout();
        }

        private void SetSelection(Staff s, Note n)
        {
            SelectedNote = n;
            SelectedNoteStaff = s;
            if (n != null) PauseLocomotion(); else ResumeLocomotion();
        }

        private void SetupInput()
        {
            nudgeAction = new InputAction("VRComposer Nudge", InputActionType.Value, expectedControlType: "Vector2");
            nudgeAction.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
            nudgeAction.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
                .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            nudgeAction.Enable();

            backAction = new InputAction("VRComposer Deselect", InputActionType.Button);
            backAction.AddBinding("<XRController>{RightHand}/{SecondaryButton}");
            backAction.AddBinding("<Keyboard>/escape");
            backAction.performed += _ => Deselect();
            backAction.Enable();

            playAction = new InputAction("VRComposer Play", InputActionType.Button);
            playAction.AddBinding("<XRController>{RightHand}/{PrimaryButton}");
            playAction.AddBinding("<Keyboard>/space");
            playAction.performed += _ => { if (started) Player.TogglePlay(); };
            playAction.Enable();
        }

        private void HandleNudge()
        {
            if (SelectedNote == null) { heldDir = Vector2Int.zero; return; }
            Vector2 v = nudgeAction.ReadValue<Vector2>();
            Vector2Int dir = Vector2Int.zero;
            if (Mathf.Abs(v.y) > 0.7f && Mathf.Abs(v.y) >= Mathf.Abs(v.x)) dir.y = v.y > 0 ? 1 : -1;
            else if (Mathf.Abs(v.x) > 0.7f) dir.x = v.x > 0 ? 1 : -1;

            if (dir == Vector2Int.zero) { heldDir = dir; return; }
            float now = Time.unscaledTime;
            if (dir == heldDir && now < nextRepeat) return;
            nextRepeat = now + (dir == heldDir ? 0.16f : 0.4f);
            heldDir = dir;

            var n = SelectedNote; var s = SelectedNoteStaff;
            int step = n.step, tick = n.tick;
            if (dir.y != 0 && !n.isRest)
            {
                step += dir.y;
                if (Score.IsBeginner) while (step >= Score.MinStep && step <= Score.MaxStep && !BeginnerKit.InScale(Score.Key, s.clef, step)) step += dir.y;
            }
            if (dir.x != 0)
            {
                int grid = Mathf.Max(Durations.Grid(n.Length16), View.Unit);
                tick += dir.x * grid;
                int tpb = Score.TicksPerBar;
                // jump over the bar end so the note stays inside a bar
                if (dir.x > 0 && tick / tpb != (tick + n.Length16 - 1) / tpb) tick = (tick / tpb + 1) * tpb;
                if (dir.x < 0 && tick / tpb != (tick + n.Length16 - 1) / tpb) tick = (tick + n.Length16 - 1) / tpb * tpb - n.Length16;
            }
            if (Score.Nudge(s, n, tick, step))
            {
                SelectedNote = n;
                if (!View.OnPage(n.tick)) View.ShowPageOf(n.tick);
                View.ApplySelectionVisual();
                UpdateIdleReadout();
                if (!n.isRest) Player.PreviewNote(s.program, Score.StepToMidi(s.clef, n.step, n.accidental), 0.3f);
            }
        }

        /// <summary>The thumbsticks also drive teleport/turn in the XR template: pause them while a note is selected.</summary>
        private void PauseLocomotion()
        {
            if (pausedLocomotion.Count > 0) return;
            foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                if (!b.enabled) continue;
                bool loco = b is UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionProvider;
                string tn = b.GetType().Name;
                if (loco || tn == "ControllerInputActionManager")
                {
                    b.enabled = false;
                    pausedLocomotion.Add(b);
                }
            }
        }

        private void ResumeLocomotion()
        {
            foreach (var b in pausedLocomotion) if (b != null) b.enabled = true;
            pausedLocomotion.Clear();
        }

        private void Update()
        {
            HandleNudge();
            bool playing = Player.IsPlaying;
            UIFactory.SetButton(playButton, playing ? "Stop" : "Play", playing ? UIFactory.Red : UIFactory.Green);
            UIFactory.SetButton(View.PlayButton, playing ? "Stop" : "Play", playing ? UIFactory.Red : UIFactory.Green);
            UIFactory.SetButton(wristPlay, playing ? "Stop" : "Play", playing ? UIFactory.Red : UIFactory.Green);
            if (undoButton != null) undoButton.interactable = Score.CanUndo;
            if (positionText != null)
            {
                if (!Player.IsReady)
                    positionText.text = string.IsNullOrEmpty(Player.LastError) ? "Loading sounds..." : Player.LastError;
                else
                {
                    float beat = playing ? Player.PositionBeats : 0f;
                    int bar = Mathf.FloorToInt(beat / Score.BeatsPerBar);
                    float inBar = beat - bar * Score.BeatsPerBar;
                    positionText.text = "Bar " + (bar + 1) + " / " + Score.bars + "   beat " + (Mathf.FloorToInt(inBar) + 1);
                }
            }
        }

        // ================================================================== edits from drag & drop

        public void DropOnStaff(Staff staff, List<Note> notes, DragPayload p)
        {
            SelectedStaff = Score.staves.IndexOf(staff);
            if (notes.Count == 1)
            {
                var n = notes[0];
                if (Score.Place(staff, n))
                {
                    SelectNote(staff, n, false);
                    if (!n.isRest) Player.PreviewNote(staff.program, Score.StepToMidi(staff.clef, n.step, n.accidental));
                }
            }
            else if (notes.Count > 1)
            {
                Score.PlaceMany(staff, notes);
                Deselect();
            }
        }

        public void MoveNote(Staff from, Note n, Staff to, int tick, int step)
        {
            if (from == to)
            {
                if (Score.Nudge(to, n, tick, step)) SelectNote(to, n, true);
                return;
            }
            Score.Move(from, n, to, tick, step);
            Deselect();
        }

        public void DeleteDragged(DragPayload p)
        {
            if (p.kind == DragKind.MoveNote) { Score.Remove(p.fromStaff, p.note); View.SetIdleReadout("Note deleted  ·  Undo brings it back"); }
            if (p.kind == DragKind.MoveChord) Score.RemoveChord(p.chord);
        }

        public void ApplySymbol(Staff staff, Note target, SymbolKind k)
        {
            if (Score.ApplySymbol(staff, target, k) && target != null && !target.isRest)
                Player.PreviewNote(staff.program, Score.StepToMidi(staff.clef, target.step, target.accidental));
        }

        public void DropChord(int degree, int tick, int length)
        {
            Score.PlaceChord(tick, length, degree);
            PreviewChord(degree);
        }

        public void MoveChord(ChordBlock c, int tick, int length)
        {
            Score.SaveUndo();
            Score.chords.Remove(c);
            Score.PlaceChord(tick, length, c.degree, false);
        }

        public void PreviewChord(int degree)
        {
            int bass;
            Player.PreviewChord(BeginnerKit.ChordMidi(Score.Key, degree, out bass));
        }

        // ================================================================== taps on palette tiles

        private void TapNote(Duration d, bool rest, bool dotted = false)
        {
            var staff = CurrentStaff;
            if (staff == null) return;
            int len = dotted ? (int)d * 3 / 2 : (int)d;
            int tick = 0, step = 4;
            foreach (var n in staff.notes) { if (n.End > tick) tick = n.End; if (!n.isRest) step = n.step; }
            int tpb = Score.TicksPerBar;
            if (tick / tpb != (tick + len - 1) / tpb) tick = (tick / tpb + 1) * tpb;
            if (tick + len > Score.TotalTicks)
            {
                if (Score.bars >= Score.MaxBars) { View.SetIdleReadout("The score is full (" + Score.MaxBars + " bars)"); return; }
                Score.bars++;
            }
            if (Score.IsBeginner) step = BeginnerKit.SnapToScale(Score.Key, staff.clef, step);
            var note = new Note { tick = tick, duration = d, dotted = dotted, isRest = rest, step = rest ? 4 : step };
            if (Score.Place(staff, note))
            {
                if (!View.OnPage(tick)) View.ShowPageOf(tick);
                SelectNote(staff, note, true);
            }
        }

        private void TapMotif(int motif)
        {
            var staff = CurrentStaff;
            if (staff == null) return;
            int bar, anchor;
            if (!BeginnerKit.BestPlacement(Score, staff, motif, out bar, out anchor))
            {
                if (Score.bars >= Score.MaxBars) { View.SetIdleReadout("The score is full"); return; }
                Score.bars++;
                BeginnerKit.BestPlacement(Score, staff, motif, out bar, out anchor);
            }
            int tick = bar * Score.TicksPerBar;
            Score.PlaceMany(staff, BeginnerKit.Realise(BeginnerKit.Motifs[motif], Score.Key, staff.clef, tick, anchor, Score.TicksPerBar));
            if (!View.OnPage(tick)) View.ShowPageOf(tick);
        }

        private void TapChord(int degree)
        {
            int bar = BeginnerKit.NextEmptyChordBar(Score);
            if (bar < 0) { View.SetIdleReadout("Every bar has a chord  ·  drag a chord onto a bar to replace it"); PreviewChord(degree); return; }
            DropChord(degree, bar * Score.TicksPerBar, Score.TicksPerBar);
        }

        private void TapSymbol(SymbolKind k)
        {
            if (k == SymbolKind.TrebleClef || k == SymbolKind.BassClef) { var s = CurrentStaff; if (s != null) Score.ApplySymbol(s, null, k); return; }
            if (SelectedNote == null) { View.SetIdleReadout("Select a note first (tap it), or drag the symbol onto a note"); return; }
            ApplySymbol(SelectedNoteStaff, SelectedNote, k);
        }

        // ================================================================== panels

        private void BuildScore()
        {
            scoreCanvas = UIFactory.WorldCanvas("Score Panel", new Vector2(ScoreView.Width, 400), panelRoot);
            View = scoreCanvas.gameObject.AddComponent<ScoreView>();
            View.Init(this, (RectTransform)scoreCanvas.transform);
        }

        // ---------------------------------------------------------------- palette

        private const float PalW = 470f, PalH = 790f;

        private void BuildPalette()
        {
            paletteCanvas = UIFactory.WorldCanvas("Palette Panel", new Vector2(PalW, PalH), panelRoot);
            var root = UIFactory.GlassPanel(paletteCanvas, "Palette");
            paletteContent = UIFactory.Rect("Content", root);
            UIFactory.Stretch(paletteContent);

            // bin: drop a note or chord here to delete it
            var bin = UIFactory.Img(root, "Bin", new Color(1f, 0.85f, 0.85f, 0.9f), UIFactory.Rounded, true);
            UIFactory.PlaceTopLeft(bin.rectTransform, 22, -PalH + 22 + 74, PalW - 44, 74);
            bin.gameObject.AddComponent<BinZone>();
            var hv = bin.gameObject.AddComponent<HoverFeedback>();
            hv.scale = 1.03f;
            var bt = UIFactory.Text(bin.transform, "Bin  ·  drop a note or chord here to delete it", 19, new Color(0.6f, 0.1f, 0.1f, 1f));
            UIFactory.Stretch(bt.rectTransform, 10, 0, 10, 0);

            paletteHint = UIFactory.Text(root, "", 17, UIFactory.TextDim);
            paletteHint.textWrappingMode = TextWrappingModes.Normal;
            UIFactory.PlaceTopLeft(paletteHint.rectTransform, 22, -PalH + 22 + 74 + 52, PalW - 44, 44);
        }

        private void RebuildPalette()
        {
            if (paletteContent == null) return;
            UIFactory.ClearChildren(paletteContent);
            bool beginner = Score.IsBeginner;
            var tabs = beginner ? new[] { "Notes", "Motifs", "Chords" } : new[] { "Notes", "Symbols", "Chords" };
            if (Array.IndexOf(tabs, paletteTab) < 0) paletteTab = "Notes";

            // tabs (segmented control)
            float tw = (PalW - 44 - 8 * (tabs.Length - 1)) / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
            {
                string tab = tabs[i];
                bool on = tab == paletteTab;
                var b = UIFactory.Button(paletteContent, tab, () => { paletteTab = tab; RebuildPalette(); }, on ? UIFactory.Orange : UIFactory.Dark, 21);
                UIFactory.PlaceTopLeft((RectTransform)b.transform, 22 + i * (tw + 8), -58, tw, 44);
            }

            var staff = CurrentStaff;
            HashSet<int> suggestedMotifs = new HashSet<int>(), suggestedChords = new HashSet<int>();
            if (beginner && staff != null)
            {
                foreach (var s in BeginnerKit.SuggestMotifs(Score, staff)) suggestedMotifs.Add(s.motif);
                foreach (var d in BeginnerKit.SuggestChords(Score, staff)) suggestedChords.Add(d);
            }

            var tiles = new List<Action<RectTransform>>();
            string hint;
            switch (paletteTab)
            {
                case "Motifs":
                    hint = "Drag a motif into a bar: its first note goes where you point. Orange = fits next.";
                    for (int i = 0; i < BeginnerKit.Motifs.Length; i++) { int mi = i; tiles.Add(r => MotifTile(r, mi, suggestedMotifs.Contains(mi))); }
                    break;
                case "Chords":
                    hint = "Drag a chord under a bar (left half = whole bar, right half = second half). Tap to add to the next bar.";
                    foreach (var deg in BeginnerKit.ChordDegrees) { int d = deg; tiles.Add(r => ChordTileUI(r, d, suggestedChords.Contains(d))); }
                    break;
                case "Symbols":
                    hint = "Drop a symbol onto a note (or tap it to apply to the selected note).";
                    foreach (SymbolKind k in Enum.GetValues(typeof(SymbolKind))) { var kk = k; tiles.Add(r => SymbolTile(r, kk)); }
                    break;
                default:
                    hint = beginner ? "Drag a note onto the stave: it snaps to notes that always sound good. Tap = add at the end."
                                    : "Drag onto the stave. Tap = add at the end. Select a note, then thumbstick to nudge.";
                    var values = beginner ? new[] { Duration.Whole, Duration.Half, Duration.Quarter, Duration.Eighth }
                                          : new[] { Duration.Whole, Duration.Half, Duration.Quarter, Duration.Eighth, Duration.Sixteenth };
                    foreach (var v in values) { var vv = v; tiles.Add(r => NoteTile(r, vv, false, false)); }
                    if (!beginner) { tiles.Add(r => NoteTile(r, Duration.Half, false, true)); tiles.Add(r => NoteTile(r, Duration.Quarter, false, true)); }
                    foreach (var v in values) { var vv = v; tiles.Add(r => NoteTile(r, vv, true, false)); }
                    break;
            }
            paletteHint.text = hint;

            const int cols = 3;
            const float gap = 10f, top = 116f;
            float tileW = (PalW - 44 - gap * (cols - 1)) / cols;
            float tileH = tiles.Count > 9 ? 108f : 124f;
            for (int i = 0; i < tiles.Count; i++)
            {
                var cell = UIFactory.Rect("Tile", paletteContent);
                UIFactory.PlaceTopLeft(cell, 22 + (i % cols) * (tileW + gap), -top - (i / cols) * (tileH + gap), tileW, tileH);
                tiles[i](cell);
            }
        }

        private Image TileBase(RectTransform cell, string label, bool suggested, out RectTransform art)
        {
            if (suggested)
            {
                var ring = UIFactory.Img(cell, "Suggested ring", UIFactory.Orange, UIFactory.Rounded);
                UIFactory.Stretch(ring.rectTransform, -4, -4, -4, -4);
            }
            var bg = UIFactory.Img(cell, "Tile", UIFactory.Tile, UIFactory.Rounded, true);
            UIFactory.Stretch(bg.rectTransform);
            var hv = bg.gameObject.AddComponent<HoverFeedback>();
            hv.useTint = true; hv.tint = bg; hv.normal = UIFactory.Tile; hv.hover = UIFactory.TileHover; hv.scale = 1.06f;

            art = UIFactory.Rect("Art", bg.transform);
            art.anchorMin = art.anchorMax = new Vector2(0.5f, 0.5f);
            art.sizeDelta = Vector2.zero;
            art.anchoredPosition = new Vector2(0, 10);

            var t = UIFactory.Text(bg.transform, label, 15, new Color(0.25f, 0.25f, 0.3f, 1f));
            UIFactory.PlaceTopLeft(t.rectTransform, 4, -cell.sizeDelta.y + 26, cell.sizeDelta.x - 8, 22);
            if (suggested)
            {
                var chip = UIFactory.Img(bg.transform, "Suggested", UIFactory.Orange, UIFactory.Rounded);
                UIFactory.PlaceTopLeft(chip.rectTransform, cell.sizeDelta.x - 92, 8, 86, 22);
                var ct = UIFactory.Text(chip.transform, "Suggested", 13, UIFactory.TextLight, TextAlignmentOptions.Center, FontStyles.Bold);
                UIFactory.Stretch(ct.rectTransform);
            }
            return bg;
        }

        private void NoteTile(RectTransform cell, Duration d, bool rest, bool dotted)
        {
            string label = (dotted ? "Dotted " + Durations.Name(d, rest).ToLower() : Durations.Name(d, rest));
            RectTransform art;
            var bg = TileBase(cell, label, false, out art);
            const float ls = 13f;
            if (rest)
            {
                for (int l = 0; l < 5; l++) Engraver.Box(art, "Mini line", 0, -2f * ls + l * ls, 3.6f * ls, 1.2f, new Color(0, 0, 0, 0.18f));
                var holder = UIFactory.Rect("Rest", art);
                holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
                holder.anchoredPosition = new Vector2(0, -2f * ls);
                Engraver.Note(holder, new Note { duration = d, isRest = true, dotted = dotted }, ls, UIFactory.Ink);
            }
            else
            {
                var holder = UIFactory.Rect("Note", art);
                holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
                holder.anchoredPosition = new Vector2(dotted ? -6 : 0, d == Duration.Whole ? 0 : -1.6f * ls);
                Engraver.Note(holder, new Note { duration = d, step = 2, dotted = dotted }, ls, UIFactory.Ink, false);
            }
            var src = bg.gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = rest ? DragKind.Rest : DragKind.Note, duration = d, dotted = dotted };
            src.onTap = () => TapNote(d, rest, dotted);
        }

        private void SymbolTile(RectTransform cell, SymbolKind k)
        {
            RectTransform art;
            var bg = TileBase(cell, DragSource.SymbolName(k), false, out art);
            float ls = k == SymbolKind.TrebleClef ? 9f : k == SymbolKind.Dot || k == SymbolKind.Staccato ? 26f : 14f;
            Engraver.GlyphCentred(art, Glyphs.Symbol(k), 0, 0, ls, UIFactory.Ink);
            var src = bg.gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = DragKind.Symbol, symbol = k };
            src.onTap = () => TapSymbol(k);
        }

        private void MotifTile(RectTransform cell, int index, bool suggested)
        {
            var m = BeginnerKit.Motifs[index];
            RectTransform art;
            var bg = TileBase(cell, m.name, suggested, out art);
            // mini engraving of the motif's shape
            const float ls = 5.5f;
            float w = cell.sizeDelta.x - 20f;
            for (int l = 0; l < 5; l++) Engraver.Box(art, "Mini line", 0, -2f * ls + l * ls - 4f, w, 1f, new Color(0, 0, 0, 0.18f));
            int total = m.Length16, t = 0;
            for (int i = 0; i < m.rhythm.Length; i++)
            {
                float x = -w / 2f + 8f + (w - 16f) * (t + 0.5f) / total;
                var n = new Note { duration = m.rhythm[i], dotted = m.dotted != null && m.dotted[i], isRest = m.IsRest(i), step = m.IsRest(i) ? 4 : Mathf.Clamp(2 + m.degrees[i] * 3 / 2, -1, 9) };
                var holder = UIFactory.Rect("n", art);
                holder.anchorMin = holder.anchorMax = new Vector2(0.5f, 0.5f);
                holder.anchoredPosition = new Vector2(x, -2f * ls - 4f + (n.isRest ? 0 : n.step * ls * 0.5f));
                Engraver.Note(holder, n, ls, UIFactory.Ink, false);
                t += m.Len(i);
            }
            var src = bg.gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = DragKind.Motif, motif = index };
            src.onTap = () => TapMotif(index);
        }

        private void ChordTileUI(RectTransform cell, int degree, bool suggested)
        {
            RectTransform art;
            var bg = TileBase(cell, BeginnerKit.ChordRoman(Score.Key, degree) + " chord", suggested, out art);
            var name = UIFactory.Text(art, BeginnerKit.ChordName(Score.Key, degree), 34, UIFactory.Indigo, TextAlignmentOptions.Center, FontStyles.Bold);
            name.rectTransform.anchorMin = name.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            name.rectTransform.sizeDelta = new Vector2(cell.sizeDelta.x, 50);
            var src = bg.gameObject.AddComponent<DragSource>();
            src.payloadFactory = () => new DragPayload { kind = DragKind.Chord, chordDegree = degree };
            src.onTap = () => TapChord(degree);
        }

        // ---------------------------------------------------------------- inspector

        private const float InsW = 430f, InsH = 790f;

        private void BuildInspector()
        {
            inspectorCanvas = UIFactory.WorldCanvas("Inspector Panel", new Vector2(InsW, InsH), panelRoot);
            var root = UIFactory.GlassPanel(inspectorCanvas, "Inspector");
            inspectorContent = UIFactory.Rect("Content", root);
            UIFactory.Stretch(inspectorContent);
        }

        private void RebuildInspector()
        {
            if (inspectorContent == null) return;
            UIFactory.ClearChildren(inspectorContent);
            var root = inspectorContent;
            float y = -58f;
            bool beginner = Score.IsBeginner;

            // mode (segmented)
            float half = (InsW - 44 - 8) / 2f;
            var b1 = UIFactory.Button(root, "Beginner", () => SetMode(ComposeMode.Beginner), beginner ? UIFactory.Orange : UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)b1.transform, 22, y, half, 50);
            var b2 = UIFactory.Button(root, "Advanced", () => SetMode(ComposeMode.Advanced), !beginner ? UIFactory.Orange : UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)b2.transform, 22 + half + 8, y, half, 50);
            y -= 66f;

            Row(root, ref y, "Place", ThemeManager.Title(Themes != null ? Themes.Current : VenueId.Studio), null, () => Themes.Next(), "Change");
            Row(root, ref y, "Key", beginner ? Score.Key.name + " (5 notes)" : Score.Key.name, null, () => Score.CycleKey(), "Change");
            if (!beginner) Row(root, ref y, "Time", Score.timeSigNumerator + "/" + Score.timeSigDenominator, null, () => Score.CycleTimeSignature(), "Change");
            Row(root, ref y, "Tempo", Score.tempoBpm + " bpm", () => Score.SetTempo(Score.tempoBpm - 4), () => Score.SetTempo(Score.tempoBpm + 4));
            Row(root, ref y, "Bars", Score.bars.ToString(), () => Score.SetBars(Score.bars - 1), () => Score.SetBars(Score.bars + 1));

            // backing toggles
            var lbl = UIFactory.Text(root, "Backing band", 19, UIFactory.TextDim, TextAlignmentOptions.Left);
            UIFactory.PlaceTopLeft(lbl.rectTransform, 22, y, 150, 50);
            var bc = UIFactory.Button(root, Score.backingChords ? "Chords on" : "Chords off", () => { Score.backingChords = !Score.backingChords; Score.NotifyChanged(); }, Score.backingChords ? UIFactory.Indigo : UIFactory.Dark, 19);
            UIFactory.PlaceTopLeft((RectTransform)bc.transform, 170, y, 116, 50);
            var bd = UIFactory.Button(root, Score.backingDrums ? "Drums on" : "Drums off", () => { Score.backingDrums = !Score.backingDrums; Score.NotifyChanged(); }, Score.backingDrums ? UIFactory.Indigo : UIFactory.Dark, 19);
            UIFactory.PlaceTopLeft((RectTransform)bd.transform, 294, y, 114, 50);
            y -= 70f;

            var title = UIFactory.Text(root, beginner ? "Melody instrument" : "Instruments", 22, UIFactory.TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.PlaceTopLeft(title.rectTransform, 22, y, InsW - 44, 32);
            y -= 42f;

            int shown = beginner ? Mathf.Min(1, Score.staves.Count) : Score.staves.Count;
            for (int i = 0; i < shown; i++)
            {
                var staff = Score.staves[i];
                int index = i;
                bool sel = i == SelectedStaff;
                var nameBtn = UIFactory.Button(root, staff.name + "   >", () =>
                {
                    SelectedStaff = index;
                    int next = (Instruments.IndexOfProgram(staff.program) + 1) % Instruments.All.Length;
                    Score.SetInstrument(staff, Instruments.All[next]);
                    Player.PreviewNote(staff.program, Score.StepToMidi(staff.clef, 4));
                }, sel && !beginner ? new Color(0.6f, 0.3f, 0.1f, 1f) : UIFactory.Dark, 21);
                UIFactory.PlaceTopLeft((RectTransform)nameBtn.transform, 22, y, InsW - 44 - (beginner ? 0 : 62), 50);
                UIFactory.LabelOf(nameBtn).alignment = TextAlignmentOptions.Left;
                if (!beginner)
                {
                    var del = UIFactory.Button(root, "x", () => Score.RemoveStaff(staff), UIFactory.Red, 24);
                    UIFactory.PlaceTopLeft((RectTransform)del.transform, InsW - 22 - 54, y, 54, 50);
                }
                y -= 58f;
            }
            if (!beginner && Score.staves.Count < Score.MaxStaves)
            {
                var add = UIFactory.Button(root, "+ Add instrument", () =>
                {
                    var def = Instruments.All[(Score.staves.Count) % Instruments.All.Length];
                    if (Score.AddStaff(def) != null) SelectStaff(Score.staves.Count - 1);
                }, new Color(0.6f, 0.3f, 0.1f, 1f), 21);
                UIFactory.PlaceTopLeft((RectTransform)add.transform, 22, y, InsW - 44, 50);
                y -= 58f;
            }
            var hint = UIFactory.Text(root, "Tap a name to change the instrument.", 16, UIFactory.TextDim, TextAlignmentOptions.Left);
            UIFactory.PlaceTopLeft(hint.rectTransform, 22, y - 4, InsW - 44, 24);
        }

        /// <summary>Label · value · [-] [+] (or one wide button).</summary>
        private void Row(RectTransform root, ref float y, string label, string value, Action minus, Action plus, string plusLabel = "+")
        {
            var l = UIFactory.Text(root, label, 19, UIFactory.TextDim, TextAlignmentOptions.Left);
            UIFactory.PlaceTopLeft(l.rectTransform, 22, y, 90, 50);
            var v = UIFactory.Text(root, value, 22, UIFactory.TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.PlaceTopLeft(v.rectTransform, 112, y, 190, 50);
            if (minus != null)
            {
                var m = UIFactory.Button(root, "-", minus, UIFactory.Dark, 30);
                UIFactory.PlaceTopLeft((RectTransform)m.transform, InsW - 22 - 54 - 8 - 54, y, 54, 50);
            }
            float pw = plusLabel.Length > 1 ? 110f : 54f;
            var p = UIFactory.Button(root, plusLabel, plus, UIFactory.Dark, plusLabel.Length > 1 ? 19 : 30);
            UIFactory.PlaceTopLeft((RectTransform)p.transform, InsW - 22 - pw, y, pw, 50);
            y -= 60f;
        }

        // ---------------------------------------------------------------- transport

        private void BuildTransport()
        {
            const float w = 1000f, h = 120f;
            transportCanvas = UIFactory.WorldCanvas("Transport Panel", new Vector2(w, h), panelRoot);
            var root = UIFactory.GlassPanel(transportCanvas, null);
            root.localScale = Vector3.one * (UIFactory.MetersPerPixel * 0.75f);

            float x = 20f;
            Func<float, float> next = bw => { float px = x; x += bw + 12f; return px; };
            playButton = UIFactory.Button(root, "Play", () => Player.TogglePlay(), UIFactory.Green, 30);
            UIFactory.PlaceTopLeft((RectTransform)playButton.transform, next(160), -18, 160, 84);
            loopButton = UIFactory.Button(root, "Loop: off", () => { Player.loop = !Player.loop; UIFactory.SetButton(loopButton, Player.loop ? "Loop: on" : "Loop: off", Player.loop ? UIFactory.Orange : UIFactory.Dark); }, UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)loopButton.transform, next(130), -18, 130, 84);
            undoButton = UIFactory.Button(root, "Undo", () => { Score.Undo(); Deselect(); }, UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)undoButton.transform, next(110), -18, 110, 84);
            var clear = UIFactory.Button(root, "Clear", () => { Player.Stop(); Score.ClearNotes(); Deselect(); }, UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)clear.transform, next(110), -18, 110, 84);
            var recenter = UIFactory.Button(root, "Recenter", Recenter, UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)recenter.transform, next(130), -18, 130, 84);

            positionText = UIFactory.Text(root, "", 22, UIFactory.TextLight, TextAlignmentOptions.Left);
            UIFactory.PlaceTopLeft(positionText.rectTransform, x + 6, -40, w - x - 26, 40);
        }

        // ---------------------------------------------------------------- welcome

        private VenueId welcomeTheme = VenueId.Studio;

        private void BuildWelcome()
        {
            welcomeCanvas = UIFactory.WorldCanvas("Welcome Panel", new Vector2(980f, 620f), panelRoot);
            UIFactory.GlassPanel(welcomeCanvas, null);
            welcomeContent = UIFactory.Rect("Content", welcomeCanvas.transform);
            UIFactory.Stretch(welcomeContent);
            RebuildWelcome();
        }

        private void RebuildWelcome()
        {
            UIFactory.ClearChildren(welcomeContent);
            var root = welcomeContent;
            const float W = 980f;
            var title = UIFactory.Text(root, "VR Composer", 54, UIFactory.TextLight, TextAlignmentOptions.Center, FontStyles.Bold);
            UIFactory.PlaceTopLeft(title.rectTransform, 0, -30, W, 64);
            var sub = UIFactory.Text(root, "Drag real notation onto a stave and hear it played, exactly as you wrote it.", 21, UIFactory.TextDim);
            UIFactory.PlaceTopLeft(sub.rectTransform, 0, -96, W, 30);

            var l1 = UIFactory.Text(root, "1.  Pick a place", 22, UIFactory.TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.PlaceTopLeft(l1.rectTransform, 40, -150, 400, 32);
            float bw = (W - 80 - 4 * 10) / 5f;
            for (int i = 0; i < ThemeManager.Order.Length; i++)
            {
                var v = ThemeManager.Order[i];
                bool on = v == welcomeTheme;
                var b = UIFactory.Button(root, ThemeManager.Title(v), () => { welcomeTheme = v; RebuildWelcome(); }, on ? UIFactory.Orange : UIFactory.Dark, 18);
                UIFactory.PlaceTopLeft((RectTransform)b.transform, 40 + i * (bw + 10), -190, bw, 60);
            }
            var desc = UIFactory.Text(root, ThemeManager.Subtitle(welcomeTheme), 17, UIFactory.TextDim);
            UIFactory.PlaceTopLeft(desc.rectTransform, 0, -258, W, 26);

            var l2 = UIFactory.Text(root, "2.  How do you want to compose?", 22, UIFactory.TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
            UIFactory.PlaceTopLeft(l2.rectTransform, 40, -300, 600, 32);
            float cw = (W - 80 - 20) / 2f;
            ModeCard(root, 40, -342, cw, "Beginner", "Ready-made motifs and chord blocks.\nEvery note snaps to a scale that always sounds good.\nYou still decide where everything goes.", ComposeMode.Beginner, UIFactory.Green);
            ModeCard(root, 40 + cw + 20, -342, cw, "Advanced", "Write note by note with full notation:\nsharps and flats, dots, 16ths, articulations,\ndynamics and several instruments.", ComposeMode.Advanced, UIFactory.Indigo);
        }

        private void ModeCard(RectTransform root, float x, float y, float w, string title, string text, ComposeMode mode, Color colour)
        {
            var b = UIFactory.Button(root, "", () =>
            {
                SetMode(mode);
                ShowComposer(true);
                if (Themes.Current != welcomeTheme) Themes.Set(welcomeTheme);
                Recenter();
            }, colour, 20);
            UIFactory.PlaceTopLeft((RectTransform)b.transform, x, y, w, 240);
            var t = UIFactory.Text(b.transform, title, 34, UIFactory.TextLight, TextAlignmentOptions.Center, FontStyles.Bold);
            UIFactory.PlaceTopLeft(t.rectTransform, 0, -22, w, 44);
            var d = UIFactory.Text(b.transform, text, 18, new Color(1, 1, 1, 0.85f), TextAlignmentOptions.Center);
            d.textWrappingMode = TextWrappingModes.Normal;
            UIFactory.PlaceTopLeft(d.rectTransform, 16, -80, w - 32, 140);
        }

        // ---------------------------------------------------------------- wrist menu

        private void BuildWristMenu()
        {
            var anchor = wristAnchor != null ? wristAnchor : FindLeftHand();
            if (anchor == null)
            {
                Debug.Log("[VRComposer] No left controller/hand found, wrist menu skipped (Recenter is on the transport).");
                return;
            }

            wristCanvas = UIFactory.WorldCanvas("Wrist Menu", new Vector2(300f, 230f), anchor);
            wristCanvas.transform.localPosition = wristLocalPosition;
            wristCanvas.transform.localRotation = Quaternion.Euler(wristLocalEuler);
            wristCanvas.transform.localScale = Vector3.one * (UIFactory.MetersPerPixel * 0.5f);
            var root = UIFactory.GlassPanel(wristCanvas, null);

            wristPlay = UIFactory.Button(root, "Play", () => Player.TogglePlay(), UIFactory.Green, 26);
            UIFactory.PlaceTopLeft((RectTransform)wristPlay.transform, 12, -12, 134, 60);
            wristHide = UIFactory.Button(root, "Hide", TogglePanels, UIFactory.Dark, 24);
            UIFactory.PlaceTopLeft((RectTransform)wristHide.transform, 154, -12, 134, 60);
            wristMode = UIFactory.Button(root, Score.IsBeginner ? "Beginner" : "Advanced", () => SetMode(Score.IsBeginner ? ComposeMode.Advanced : ComposeMode.Beginner), UIFactory.Indigo, 22);
            UIFactory.PlaceTopLeft((RectTransform)wristMode.transform, 12, -84, 134, 60);
            var rc = UIFactory.Button(root, "Recenter", Recenter, UIFactory.Dark, 22);
            UIFactory.PlaceTopLeft((RectTransform)rc.transform, 154, -84, 134, 60);
            wristTheme = UIFactory.Button(root, ThemeManager.Title(Themes.Current), () => Themes.Next(), UIFactory.Indigo, 20);
            UIFactory.PlaceTopLeft((RectTransform)wristTheme.transform, 12, -156, 276, 60);
        }

        private static Transform FindLeftHand()
        {
            string[] names = { "Left Controller", "LeftHand Controller", "Left Hand", "LeftHand", "Left Hand Interaction Visual" };
            foreach (var n in names)
            {
                var go = GameObject.Find(n);
                if (go != null) return go.transform;
            }
            return null;
        }
    }
}
