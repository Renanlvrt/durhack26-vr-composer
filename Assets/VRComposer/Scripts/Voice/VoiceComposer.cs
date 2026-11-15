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
    ///   [Sing]  [16ths/8ths]  [Mic: Headset]  [Talk (hold Y)]  status
    /// Sing: press, then start singing whenever you like. Your first note is beat 1 of the next bar
    /// (after the last note of the selected staff); controller pulses keep the beat from there.
    /// Every 4 beats the notes sung so far are written onto the staff. 4 beats of silence end the
    /// take (or press Sing again). The whole take is ONE Undo step.
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

        [Tooltip("Snap sung notes to 8ths instead of 16ths (more forgiving).")]
        public bool eighthGrid = false;
        [Tooltip("Beats of silence that end the take.")]
        public int stopAfterSilentBeats = 4;
        [Tooltip("Write the notes sung so far onto the staff every this many beats.")]
        public int writeEveryBeats = 4;
        [Tooltip("Give up if no note is sung within this many seconds of pressing Sing.")]
        public float waitForFirstNoteSeconds = 20f;
        [Tooltip("Hold Y this long before the composer listens (a quicker tap stays Back / deselect).")]
        public float talkHoldSeconds = 0.25f;

        public MicRecorder Mic { get; private set; }
        public ElevenAgent Agent { get; private set; }
        public bool Busy { get { return state != State.Idle; } }

        private enum State { Idle, Waiting, Recording, Processing }
        private State state;
        private VRComposerApp app;

        // take
        private readonly List<float> take = new List<float>();
        private long takeStartAbs;
        private double t0, t1, beatSec;
        private int lastBeat, startTick, maxBars, lastWriteBeat;
        private Staff takeStaff;
        private float noiseFloorDb, lastLoud, peakDb;
        private int noiseCount;
        private bool undoSaved;
        private Task<TakeResult> analysing;   // live (every 4 beats) or final
        private int analysingLimitTicks;      // -1 = final: write everything
        private int lastPlaced;

        // ui
        private Button singButton, gridButton, micButton, talkButton;
        private TextMeshProUGUI statusText;
        private InputAction talkAction, singAction;
        private bool talkHeld;
        private float talkPressedAt;

        private class TakeResult
        {
            public List<SungNote> notes;   // seconds from the first note
            public double tuningCents;
            public string error;
        }

        // ================================================================== lifecycle

        private void Awake()
        {
            Instance = this;
            app = GetComponent<VRComposerApp>();
            Mic = gameObject.AddComponent<MicRecorder>();
            Agent = gameObject.AddComponent<ElevenAgent>();
            eighthGrid = PlayerPrefs.GetInt("VRComposer.EighthGrid", 0) == 1;
            string savedMic = PlayerPrefs.GetString("VRComposer.MicDevice", "");
            if (!string.IsNullOrEmpty(savedMic)) Mic.deviceName = savedMic;
        }

        private void Start()
        {
            BuildPanel();
            // Hold Y (left controller) or T to talk to the ElevenLabs composer; a quick tap of Y stays Back.
            talkAction = new InputAction("VRComposer Talk", InputActionType.Button);
            talkAction.AddBinding("<XRController>{LeftHand}/{SecondaryButton}");
            talkAction.AddBinding("<Keyboard>/t");
            talkAction.started += _ => { talkPressedAt = Time.unscaledTime; talkHeld = true; };
            talkAction.canceled += _ => { talkHeld = false; if (Agent.Talking) Agent.PushToTalk(false); };
            talkAction.Enable();
            singAction = new InputAction("VRComposer Sing", InputActionType.Button, "<Keyboard>/v");
            singAction.performed += _ => ToggleSing();
            singAction.Enable();
            Agent.StatusChanged += s => { if (state == State.Idle) SetStatus(s); };
            Mic.DeviceChanged += d => UIFactory.SetButton(micButton, "Mic: " + MicRecorder.ShortName(d), UIFactory.Dark);
            Mic.Begin();   // warm: the first take must not lose its start (and on Quest this asks for permission)
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
            if (state == State.Idle) BeginTake();
            else if (state == State.Waiting) CancelTake("Cancelled");
            else if (state == State.Recording) FinishTake();
        }

        /// <summary>For the agent: starts a take once its spoken reply has finished.</summary>
        public string StartSingingFromAgent()
        {
            if (state != State.Idle) return "Already recording.";
            pendingAgentSing = true;
            pendingSince = Time.unscaledTime;
            pendingAudioMark = Agent.AudioChunks;
            return "Recording starts when you finish speaking; the user's first note starts the take.";
        }
        private bool pendingAgentSing;
        private float pendingSince;
        private int pendingAudioMark;

        // ================================================================== take

        private void BeginTake()
        {
            if (!Mic.Begin() || Mic.TotalSamples == 0)
            {
                if (Mic.IsOn) { SetStatus("Microphone starting... press Sing again"); return; }
                SetStatus(Application.platform == RuntimePlatform.Android ? "Allow the microphone, then press Sing again" : "No microphone found");
                return;
            }
            if (Agent != null) { Agent.PushToTalk(false); Agent.Hush(); }
            if (app.Player.IsPlaying) app.Player.Stop();
            var score = app.Score;
            takeStaff = score.staves.Count > 0 ? score.staves[Mathf.Clamp(app.SelectedStaff, 0, score.staves.Count - 1)] : null;
            if (takeStaff == null) { SetStatus("Add an instrument first"); return; }

            int tpb = score.TicksPerBar;
            int lastEnd = 0;
            foreach (var n in takeStaff.notes) lastEnd = Mathf.Max(lastEnd, n.End);
            startTick = Mathf.CeilToInt(lastEnd / (float)tpb) * tpb;          // next barline
            maxBars = Score.MaxBars - startTick / tpb;
            if (maxBars <= 0) { SetStatus("The score is full (" + Score.MaxBars + " bars)"); return; }

            beatSec = 60.0 / score.tempoBpm;
            take.Clear();
            takeStartAbs = Mic.TotalSamples;
            Mic.Chunk += OnMicChunk;
            t0 = Time.realtimeSinceStartupAsDouble;
            lastBeat = -1;
            lastWriteBeat = 0;
            lastPlaced = 0;
            undoSaved = false;
            noiseFloorDb = -120f; noiseCount = 0; peakDb = -120f;
            analysing = null;
            state = State.Waiting;
            UIFactory.SetButton(singButton, "Stop", UIFactory.Red);
            SetStatus("Listening (mic: " + MicRecorder.ShortName(Mic.CurrentDevice) + ")... start singing whenever you are ready");
        }

        private void OnMicChunk(float[] samples, long start)
        {
            if (state != State.Waiting && state != State.Recording) return;
            int skip = (int)Math.Max(0, takeStartAbs - start);
            for (int i = skip; i < samples.Length; i++) take.Add(samples[i]);
        }

        private void CancelTake(string why)
        {
            Mic.Chunk -= OnMicChunk;
            state = State.Idle;
            analysing = null;
            ResetButtons();
            SetStatus(why);
        }

        private void FinishTake()
        {
            Mic.Chunk -= OnMicChunk;
            state = State.Processing;
            UIFactory.SetButton(singButton, "...", UIFactory.Dark);
            SetStatus("Writing the last notes...");
            var audio = take.ToArray();
            SaveTake(audio);
            // the final pass waits for a running live pass to finish (see Update)
            finalAudio = audio;
        }
        private float[] finalAudio;

        private void StartAnalysis(float[] audio, int limitTicks)
        {
            analysingLimitTicks = limitTicks;
            analysing = Task.Run(() => Analyse(audio));
        }

        private static TakeResult Analyse(float[] audio)
        {
            try
            {
                var frames = new YinTracker().Track(audio, MicRecorder.Rate);
                double tuning;
                var notes = NoteSegmenter.Segment(frames, new SegmenterSettings(), out tuning);
                // the first sung note is beat 1: everything is timed from it (this also cancels the mic delay)
                if (notes.Count > 0)
                {
                    double first = notes[0].start;
                    foreach (var n in notes) { n.start -= first; n.end -= first; }
                }
                return new TakeResult { notes = notes, tuningCents = tuning };
            }
            catch (Exception e) { return new TakeResult { error = e.Message }; }
        }

        private void Update()
        {
            if (talkHeld && !Agent.Talking && Time.unscaledTime - talkPressedAt >= talkHoldSeconds && state == State.Idle) Agent.PushToTalk(true);
            // start a take asked for by the agent once its reply has played (or 5 s without any reply)
            if (pendingAgentSing && state == State.Idle)
            {
                bool replied = Agent.AudioChunks > pendingAudioMark;
                if ((replied && !Agent.Speaking && Agent.SilentFor > 0.4f) || Time.unscaledTime - pendingSince > 5f)
                { pendingAgentSing = false; BeginTake(); }
            }

            double now = Time.realtimeSinceStartupAsDouble;
            float level = Mic.LevelDb;

            if (state == State.Waiting)
            {
                double el = now - t0;
                // learn the room for a moment, then the first clearly louder sound starts the take
                if (el < 0.3) { if (level > -119f) { noiseFloorDb = (noiseFloorDb * noiseCount + level) / (noiseCount + 1); noiseCount++; } }
                else if (level > Mathf.Max(noiseFloorDb + 15f, -55f))
                {
                    state = State.Recording;
                    t1 = now;
                    lastLoud = 0f;
                    peakDb = level;
                }
                else if (el > waitForFirstNoteSeconds)
                    CancelTake("Heard nothing. Check the mic (" + MicRecorder.ShortName(Mic.CurrentDevice) + "): tap Mic to switch");
            }
            else if (state == State.Recording)
            {
                double rel = now - t1;
                int beat = (int)Math.Floor(rel / beatSec);
                int bpb = Mathf.Max(1, Mathf.RoundToInt(app.Score.BeatsPerBar));
                if (beat != lastBeat)
                {
                    lastBeat = beat;
                    bool down = beat % bpb == 0;
                    Buzz(down ? 0.6f : 0.25f, down ? 0.05f : 0.025f);
                }
                peakDb = Mathf.Max(peakDb, level);
                if (level > Mathf.Max(noiseFloorDb + 12f, peakDb - 28f)) lastLoud = (float)rel;

                int bar = beat / bpb;
                SetStatus("REC  bar " + (startTick / app.Score.TicksPerBar + bar + 1) + "  beat " + (beat % bpb + 1)
                    + (lastPlaced > 0 ? "   ·  " + lastPlaced + " notes so far" : "") + "   (stops after " + stopAfterSilentBeats + " quiet beats)");

                // every 4 beats: write what has been sung so far (only notes that have ended)
                if (beat >= lastWriteBeat + writeEveryBeats && analysing == null)
                {
                    lastWriteBeat = beat - beat % writeEveryBeats;
                    StartAnalysis(take.ToArray(), lastWriteBeat * 4);
                }

                bool quiet = rel - lastLoud > stopAfterSilentBeats * beatSec;
                if (quiet || bar >= maxBars || rel > 90) FinishTake();
            }

            // results
            if (analysing != null && analysing.IsCompleted)
            {
                var r = analysing.Result;
                int limit = analysingLimitTicks;
                analysing = null;
                if (r.error != null) Debug.LogWarning("[VRComposer] Take analysis failed: " + r.error);
                else if (state == State.Recording || state == State.Processing) WriteNotes(r, limit);
            }
            if (state == State.Processing && analysing == null && finalAudio != null)
            {
                var audio = finalAudio;
                finalAudio = null;
                StartAnalysis(audio, -1);
                finalPending = true;
            }
            else if (state == State.Processing && analysing == null && finalPending)
            {
                finalPending = false;
                state = State.Idle;
                ResetButtons();
                SetStatus(lastPlaced > 0 ? finalSummary : "No notes heard. Sing louder or closer to the mic (" + MicRecorder.ShortName(Mic.CurrentDevice) + ")");
            }
        }
        private bool finalPending;
        private string finalSummary = "";

        /// <summary>Writes the take's notes up to 'limitTicks' (from the first note; -1 = all), replacing
        /// what earlier passes of the same take wrote. The whole take stays one Undo step.</summary>
        private void WriteNotes(TakeResult r, int limitTicks)
        {
            var score = app.Score;
            if (takeStaff == null || !score.staves.Contains(takeStaff)) return;
            if (r.notes.Count == 0) return;
            var ticks = RhythmQuantizer.Quantize(r.notes, score.tempoBpm, score.TicksPerBar, eighthGrid ? 2 : 1, true);
            if (limitTicks >= 0)
            {
                // live pass: only notes that have finished before the limit; no trailing rest
                ticks.RemoveAll(t => t.tick + t.length > limitTicks);
                while (ticks.Count > 0 && ticks[ticks.Count - 1].midi < 0) ticks.RemoveAt(ticks.Count - 1);
                if (ticks.Count > 0) { var l = ticks[ticks.Count - 1]; l.tieToNext = false; ticks[ticks.Count - 1] = l; }
                if (ticks.Count == 0) return;
            }
            var midis = new List<int>();
            foreach (var t in ticks) if (t.midi >= 0) midis.Add(t.midi);
            int shift = StaveWriter.BestOctaveShift(score, takeStaff.clef, midis);
            for (int i = 0; i < ticks.Count; i++)
            {
                var t = ticks[i];
                if (t.midi >= 0) { t.midi += shift; ticks[i] = t; }
            }
            // replace what earlier passes of this take wrote (everything from the take's first bar)
            if (undoSaved) takeStaff.notes.RemoveAll(n => n.tick >= startTick);
            // sung notes keep the exact pitch you sang (sharps/flats included), even in Beginner mode
            lastPlaced = StaveWriter.Write(score, takeStaff, ticks, startTick, false, !undoSaved, false);
            undoSaved = true;
            app.Deselect();
            score.NotifyChanged();
            string tune = Math.Abs(r.tuningCents) < 8 ? "in tune" : (r.tuningCents < 0 ? "sang " + Math.Round(-r.tuningCents) + " cents flat" : "sang " + Math.Round(r.tuningCents) + " cents sharp");
            string oct = shift == 0 ? "" : "  ·  moved " + (shift > 0 ? "up " : "down ") + Math.Abs(shift / 12) + " octave" + (Math.Abs(shift) > 12 ? "s" : "");
            finalSummary = lastPlaced + " notes added to " + takeStaff.name + " from bar " + (startTick / score.TicksPerBar + 1) + "  ·  " + tune + oct + "  ·  Undo removes them";
        }

        private void SaveTake(float[] audio)
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "takes");
                string name = "take_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                MicRecorder.SaveWav(System.IO.Path.Combine(dir, name + ".wav"), audio);
                var meta = "{\"bpm\":" + app.Score.tempoBpm + ",\"ticksPerBar\":" + app.Score.TicksPerBar
                    + ",\"anchor\":\"first note\",\"mic\":" + Newtonsoft.Json.JsonConvert.ToString(Mic.CurrentDevice ?? "default") + "}";
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
            micButton = UIFactory.Button(root, "Mic: " + MicRecorder.ShortName(Mic.CurrentDevice), () =>
            {
                if (state != State.Idle) return;
                Mic.NextDevice();
                PlayerPrefs.SetString("VRComposer.MicDevice", Mic.CurrentDevice ?? "");
                SetStatus("Mic: " + (Mic.CurrentDevice ?? "default"));
            }, UIFactory.Dark, 18);
            UIFactory.PlaceTopLeft((RectTransform)micButton.transform, next(150), -14, 150, 72);
            talkButton = UIFactory.Button(root, "Talk (hold Y)", null, UIFactory.Indigo, 20);
            UIFactory.PlaceTopLeft((RectTransform)talkButton.transform, next(160), -14, 160, 72);
            var hold = talkButton.gameObject.AddComponent<HoldButton>();
            hold.onDown = () => Agent.PushToTalk(true);
            hold.onUp = () => Agent.PushToTalk(false);

            statusText = UIFactory.Text(root, "Sing (V), then start singing: notes appear every 4 beats  ·  hold Y (T) to ask the composer", 19, UIFactory.TextLight, TextAlignmentOptions.Left);
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
