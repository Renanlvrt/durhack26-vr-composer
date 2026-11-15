using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace VRComposer.Voice
{
    /// <summary>
    /// Sing to notes + the ElevenLabs voice composer. Adds itself next to VRComposerApp at start and
    /// builds a small "Voice" strip under the transport:
    ///   [Sing]  [16ths/8ths]  [Calibrate]  [Talk (hold Y)]  status
    /// Sing: one bar of count-in (beat numbers + controller pulses, no sound so nothing bleeds into the
    /// mic), then sing "la la laa"; it stops by itself after two quiet bars (or press Sing again).
    /// The notes are appended from the next barline after the last note of the selected staff, as
    /// one Undo step.
    /// </summary>
    public class VoiceComposer : MonoBehaviour
    {
        public static VoiceComposer Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var app = VRComposerApp.Instance;
            if (app != null && app.GetComponent<VoiceComposer>() == null) app.gameObject.AddComponent<VoiceComposer>();
        }

        [Tooltip("Mic delay (seconds) subtracted from every sung note. Set by Calibrate.")]
        public float latency = 0.08f;
        [Tooltip("Snap sung notes to 8ths instead of 16ths (more forgiving).")]
        public bool eighthGrid = false;
        [Tooltip("Quiet bars after singing before the take stops by itself.")]
        public int autoStopBars = 2;

        public MicRecorder Mic { get; private set; }
        public ElevenAgent Agent { get; private set; }
        public bool Busy { get { return state != State.Idle; } }

        private enum State { Idle, CountIn, Recording, Processing }
        private State state;
        private bool calibrating;
        private VRComposerApp app;

        // take
        private readonly List<float> take = new List<float>();
        private long takeStartAbs;
        private double t0, beatSec, countInSec;
        private int beatsPerBar, lastBeat, startTick, maxBars;
        private Staff takeStaff;
        private float noiseFloorDb = 0f, lastLoud;
        private bool sungSomething;
        private Task<TakeResult> processing;

        // ui
        private Button singButton, gridButton, calButton, talkButton;
        private TextMeshProUGUI statusText;
        private InputAction talkAction, singAction;

        private class TakeResult
        {
            public List<SungNote> notes;
            public double tuningCents;
            public List<double> onsets;   // calibration
            public string error;
        }

        // ================================================================== lifecycle

        private void Awake()
        {
            Instance = this;
            app = GetComponent<VRComposerApp>();
            Mic = gameObject.AddComponent<MicRecorder>();
            Agent = gameObject.AddComponent<ElevenAgent>();
            latency = PlayerPrefs.GetFloat("VRComposer.MicLatency", latency);
            eighthGrid = PlayerPrefs.GetInt("VRComposer.EighthGrid", 0) == 1;
        }

        private void Start()
        {
            BuildPanel();
            // Hold Y (left controller) or T to talk to the ElevenLabs composer. A tap of Y still deselects
            // (VRComposerApp's Back), which is harmless while talking.
            talkAction = new InputAction("VRComposer Talk", InputActionType.Button);
            talkAction.AddBinding("<XRController>{LeftHand}/{SecondaryButton}");
            talkAction.AddBinding("<Keyboard>/t");
            talkAction.started += _ => Agent.PushToTalk(true);
            talkAction.canceled += _ => Agent.PushToTalk(false);
            talkAction.Enable();
            singAction = new InputAction("VRComposer Sing", InputActionType.Button, "<Keyboard>/v");
            singAction.performed += _ => ToggleSing();
            singAction.Enable();
            Agent.StatusChanged += s => { if (state == State.Idle) SetStatus(s); };
        }

        private void OnDestroy()
        {
            talkAction?.Dispose();
            singAction?.Dispose();
            if (Instance == this) Instance = null;
        }

        // ================================================================== public

        public void ToggleSing()
        {
            if (state == State.Idle) BeginTake(false);
            else if (state == State.CountIn || state == State.Recording) EndTake();
        }

        public void Calibrate()
        {
            if (state == State.Idle) BeginTake(true);
        }

        /// <summary>For the agent: starts a take once its spoken reply has finished.</summary>
        public string StartSingingFromAgent()
        {
            if (state != State.Idle) return "Already recording.";
            pendingAgentSing = true;
            return "Count-in starts when I finish speaking: one bar, then sing.";
        }
        private bool pendingAgentSing;

        // ================================================================== take

        private void BeginTake(bool calibration)
        {
            if (!Mic.Begin())
            {
                SetStatus(Application.platform == RuntimePlatform.Android ? "Allow the microphone, then press Sing again" : "No microphone found");
                return;
            }
            if (Agent != null) Agent.PushToTalk(false);
            if (app.Player.IsPlaying) app.Player.Stop();
            calibrating = calibration;
            var score = app.Score;
            takeStaff = score.staves.Count > 0 ? score.staves[Mathf.Clamp(app.SelectedStaff, 0, score.staves.Count - 1)] : null;
            if (takeStaff == null && !calibration) { SetStatus("Add an instrument first"); return; }

            int tpb = score.TicksPerBar;
            int lastEnd = 0;
            if (takeStaff != null) foreach (var n in takeStaff.notes) lastEnd = Mathf.Max(lastEnd, n.End);
            startTick = Mathf.CeilToInt(lastEnd / (float)tpb) * tpb;          // next barline
            maxBars = Score.MaxBars - startTick / tpb;
            if (!calibration && maxBars <= 0) { SetStatus("The score is full (" + Score.MaxBars + " bars)"); return; }

            beatsPerBar = Mathf.Max(1, Mathf.RoundToInt(score.BeatsPerBar));
            beatSec = 60.0 / score.tempoBpm;
            countInSec = beatsPerBar * beatSec;
            take.Clear();
            takeStartAbs = Mic.TotalSamples;
            Mic.Chunk += OnMicChunk;
            t0 = Time.realtimeSinceStartupAsDouble;
            lastBeat = -1;
            sungSomething = false;
            noiseFloorDb = 0f;
            state = State.CountIn;
            UIFactory.SetButton(singButton, "Stop", UIFactory.Red);
        }

        private void OnMicChunk(float[] samples, long start)
        {
            if (state != State.CountIn && state != State.Recording) return;
            int skip = (int)Math.Max(0, takeStartAbs - start);
            for (int i = skip; i < samples.Length; i++) take.Add(samples[i]);
        }

        private void EndTake()
        {
            Mic.Chunk -= OnMicChunk;
            if (state == State.CountIn && !calibrating) { state = State.Idle; ResetButtons(); SetStatus("Cancelled"); return; }
            state = State.Processing;
            UIFactory.SetButton(singButton, "...", UIFactory.Dark);
            SetStatus("Listening back...");
            var audio = take.ToArray();
            double offset = latency + countInSec;
            bool cal = calibrating;
            SaveTake(audio);
            processing = Task.Run(() => Analyse(audio, offset, cal));
        }

        private static TakeResult Analyse(float[] audio, double offset, bool calibration)
        {
            try
            {
                var frames = new YinTracker().Track(audio, MicRecorder.Rate);
                var r = new TakeResult();
                if (calibration)
                {
                    r.onsets = FindClaps(frames, offset);
                    return r;
                }
                double tuning;
                var notes = NoteSegmenter.Segment(frames, new SegmenterSettings(), out tuning);
                var kept = new List<SungNote>();
                foreach (var n in notes)
                {
                    n.start -= offset; n.end -= offset;
                    if (n.end <= 0.05) continue;                 // noise during the count-in
                    if (n.start < 0) n.start = 0;                 // came in a little early
                    kept.Add(n);
                }
                r.notes = kept;
                r.tuningCents = tuning;
                return r;
            }
            catch (Exception e) { return new TakeResult { error = e.Message }; }
        }

        /// <summary>Sharp loudness rises (claps or "ta") after the count-in, in seconds from the downbeat.</summary>
        private static List<double> FindClaps(PitchFrame[] fr, double offset)
        {
            var on = new List<double>();
            if (fr.Length < 10) return on;
            double peak = double.MinValue;
            foreach (var f in fr) peak = Math.Max(peak, f.onsetDb);
            double lastT = -1;
            for (int i = 5; i < fr.Length; i++)
            {
                double min = double.MaxValue;
                for (int k = i - 5; k < i; k++) min = Math.Min(min, fr[k].onsetDb);
                if (fr[i].onsetDb - min > 15 && fr[i].onsetDb > peak - 20 && fr[i].time - lastT > 0.15)
                {
                    lastT = fr[i].time;
                    on.Add(fr[i].time - 0.005 - offset);
                }
            }
            return on;
        }

        private void Update()
        {
            if (pendingAgentSing && state == State.Idle && !Agent.Speaking) { pendingAgentSing = false; BeginTake(false); }

            if (state == State.CountIn || state == State.Recording)
            {
                double el = Time.realtimeSinceStartupAsDouble - t0;
                int beat = (int)Math.Floor(el / beatSec);
                if (beat != lastBeat)
                {
                    lastBeat = beat;
                    bool down = beat % beatsPerBar == 0;
                    Buzz(down ? 0.7f : 0.3f, down ? 0.06f : 0.03f);
                }
                if (state == State.CountIn)
                {
                    noiseFloorDb = Mathf.Min(noiseFloorDb, Mic.LevelDb);
                    SetStatus("Count-in:  " + (beat + 1) + " / " + beatsPerBar + (calibrating ? "   then clap on each beat" : "   sing on the next 1"));
                    if (el >= countInSec) { state = State.Recording; lastLoud = (float)el; }
                }
                else
                {
                    double rel = el - countInSec;
                    int bar = (int)(rel / countInSec);
                    int b = (int)((rel - bar * countInSec) / beatSec) + 1;
                    if (Mic.LevelDb > noiseFloorDb + 18f) { sungSomething = true; lastLoud = (float)el; }
                    if (calibrating)
                    {
                        SetStatus("CLAP on each beat:  " + b + " / " + beatsPerBar);
                        if (rel >= countInSec + 0.3) EndTake();
                    }
                    else
                    {
                        SetStatus("REC  bar " + (startTick / app.Score.TicksPerBar + bar + 1) + "  beat " + b + "   (Sing / V to stop)");
                        bool quiet = sungSomething && el - lastLoud > autoStopBars * countInSec;
                        if (quiet || bar >= maxBars || rel > 60) EndTake();
                    }
                }
            }
            else if (state == State.Processing && processing != null && processing.IsCompleted)
            {
                var r = processing.Result;
                processing = null;
                state = State.Idle;
                ResetButtons();
                if (r.error != null) SetStatus("Could not read the take: " + r.error);
                else if (calibrating) ApplyCalibration(r.onsets);
                else ApplyTake(r);
            }
        }

        private void ApplyTake(TakeResult r)
        {
            var score = app.Score;
            if (r.notes.Count == 0) { SetStatus("No notes heard. Sing louder, closer to the mic, after the count-in"); return; }
            if (takeStaff == null || !score.staves.Contains(takeStaff)) { SetStatus("The staff was removed"); return; }
            var ticks = RhythmQuantizer.Quantize(r.notes, score.tempoBpm, score.TicksPerBar, eighthGrid ? 2 : 1, true);
            var midis = new List<int>();
            foreach (var t in ticks) if (t.midi >= 0) midis.Add(t.midi);
            int shift = StaveWriter.BestOctaveShift(score, takeStaff.clef, midis);
            for (int i = 0; i < ticks.Count; i++)
            {
                var t = ticks[i];
                if (t.midi >= 0) { t.midi += shift; ticks[i] = t; }
            }
            // no rests before the first note: the take starts when you start singing on a beat
            int placed = StaveWriter.Write(score, takeStaff, ticks, startTick, false);
            app.Deselect();
            score.NotifyChanged();
            string tune = Math.Abs(r.tuningCents) < 8 ? "in tune" : (r.tuningCents < 0 ? "sang " + Math.Round(-r.tuningCents) + " cents flat" : "sang " + Math.Round(r.tuningCents) + " cents sharp");
            string oct = shift == 0 ? "" : "  ·  moved " + (shift > 0 ? "up " : "down ") + Math.Abs(shift / 12) + " octave" + (Math.Abs(shift) > 12 ? "s" : "");
            SetStatus(placed + " notes added to " + takeStaff.name + " from bar " + (startTick / score.TicksPerBar + 1) + "  ·  " + tune + oct + "  ·  Undo removes them");
        }

        private void ApplyCalibration(List<double> onsets)
        {
            // onsets are relative to the downbeat with the current latency already removed
            var errs = new List<double>();
            foreach (var t in onsets)
            {
                double beat = Math.Round(t / beatSec);
                if (beat < 0 || beat >= beatsPerBar) continue;
                errs.Add(t - beat * beatSec);
            }
            if (errs.Count < 2) { SetStatus("Calibration: heard " + errs.Count + " claps. Try again, clap louder on each beat"); return; }
            errs.Sort();
            double med = errs[errs.Count / 2];
            latency = Mathf.Clamp(latency + (float)med, 0f, 0.4f);
            PlayerPrefs.SetFloat("VRComposer.MicLatency", latency);
            SetStatus("Calibrated: mic delay " + Mathf.RoundToInt(latency * 1000) + " ms (" + errs.Count + " claps)");
        }

        private void SaveTake(float[] audio)
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "takes");
                string name = (calibrating ? "calibration_" : "take_") + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                MicRecorder.SaveWav(System.IO.Path.Combine(dir, name + ".wav"), audio);
                var meta = "{\"bpm\":" + app.Score.tempoBpm + ",\"ticksPerBar\":" + app.Score.TicksPerBar
                    + ",\"countInSec\":" + countInSec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"latency\":" + latency.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name + ".json"), meta);
            }
            catch (Exception e) { Debug.LogWarning("[VRComposer] Could not save the take: " + e.Message); }
        }

        private static void Buzz(float amp, float dur)
        {
            foreach (var node in new[] { UnityEngine.XR.XRNode.LeftHand, UnityEngine.XR.XRNode.RightHand })
            {
                var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid) dev.SendHapticImpulse(0, amp, dur);
            }
        }

        // ================================================================== ui

        public void SetStatus(string s)
        {
            if (statusText != null && statusText.text != s) statusText.text = s;
            if (app != null && app.View != null && state != State.Idle) app.View.SetReadout(s);
        }

        private void ResetButtons()
        {
            UIFactory.SetButton(singButton, "Sing", UIFactory.Orange);
        }

        private void BuildPanel()
        {
            var transport = app.transform.Find("VR Composer Panels/Transport Panel");
            if (transport == null) { Debug.LogWarning("[VRComposer] Transport panel not found: no voice strip."); return; }
            const float w = 1120f, h = 100f;
            var canvas = UIFactory.WorldCanvas("Voice Panel", new Vector2(w, h), transport);
            var rt = (RectTransform)canvas.transform;
            rt.localScale = Vector3.one;                      // the transport already converts px to metres
            rt.localRotation = Quaternion.identity;
            rt.localPosition = new Vector3(0f, -(120f * 0.5f + 12f + h * 0.5f), 0f);
            var root = UIFactory.GlassPanel(canvas, null);

            float x = 20f;
            Func<float, float> next = bw => { float px = x; x += bw + 12f; return px; };
            singButton = UIFactory.Button(root, "Sing", ToggleSing, UIFactory.Orange, 26);
            UIFactory.PlaceTopLeft((RectTransform)singButton.transform, next(130), -14, 130, 72);
            gridButton = UIFactory.Button(root, eighthGrid ? "8ths" : "16ths", () =>
            {
                eighthGrid = !eighthGrid;
                PlayerPrefs.SetInt("VRComposer.EighthGrid", eighthGrid ? 1 : 0);
                UIFactory.SetButton(gridButton, eighthGrid ? "8ths" : "16ths", UIFactory.Dark);
            }, UIFactory.Dark, 20);
            UIFactory.PlaceTopLeft((RectTransform)gridButton.transform, next(90), -14, 90, 72);
            calButton = UIFactory.Button(root, "Calibrate", Calibrate, UIFactory.Dark, 20);
            UIFactory.PlaceTopLeft((RectTransform)calButton.transform, next(120), -14, 120, 72);
            talkButton = UIFactory.Button(root, "Talk (hold Y)", null, UIFactory.Indigo, 20);
            UIFactory.PlaceTopLeft((RectTransform)talkButton.transform, next(170), -14, 170, 72);
            var hold = talkButton.gameObject.AddComponent<HoldButton>();
            hold.onDown = () => Agent.PushToTalk(true);
            hold.onUp = () => Agent.PushToTalk(false);

            statusText = UIFactory.Text(root, "Sing (V): count-in, then la la la  ·  hold Y (T) to ask the composer", 19, UIFactory.TextLight, TextAlignmentOptions.Left);
            UIFactory.PlaceTopLeft(statusText.rectTransform, x + 4, -14, w - x - 24, 72);
            statusText.textWrappingMode = TextWrappingModes.Normal;
        }

        private void LateUpdate()
        {
            if (talkButton != null && Agent != null)
                UIFactory.SetButton(talkButton, Agent.Talking ? "Listening..." : "Talk (hold Y)", Agent.Talking ? UIFactory.Red : UIFactory.Indigo);
        }
    }

    /// <summary>Press-and-hold for a UI button (mouse or XR ray).</summary>
    public class HoldButton : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IPointerUpHandler
    {
        public Action onDown, onUp;
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { onDown?.Invoke(); }
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) { onUp?.Invoke(); }
    }
}
