using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MeltySynth;
using UnityEngine;

namespace VRComposer
{
    /// <summary>
    /// Plays the Score through a SoundFont with MeltySynth. Deterministic: every note placed on the
    /// staff is played exactly as written, nothing is generated. One MIDI channel per staff, with
    /// that staff's General MIDI program (flute, sax...).
    ///
    /// The SoundFont ships as Resources/&lt;soundFontResource&gt;.bytes (a renamed .sf2), because
    /// StreamingAssets cannot be read with File.* on Android / Quest.
    /// MeltySynth is not thread-safe, so every synth access goes through `gate`.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class ScorePlayer : MonoBehaviour
    {
        [Tooltip("Name of the .bytes SoundFont inside a Resources folder (without extension).")]
        public string soundFontResource = "GeneralUser-GS";

        [Range(0f, 2f)] public float volume = 0.8f;
        public bool loop = true;

        [Tooltip("Max simultaneous voices. Lower it if the Quest struggles.")]
        public int maximumPolyphony = 96;

        [Range(0, 127)] public int reverb = 50;

        public bool IsReady { get; private set; }
        public bool IsPlaying { get { return playing; } }
        public string LastError { get; private set; }

        /// <summary>Current playback position in quarter-note beats of the score (a fermata's extra
        /// time is taken out, so the playhead waits on the held note instead of running ahead).</summary>
        public float PositionBeats
        {
            get
            {
                lock (gate)
                {
                    if (samplesPerBeat <= 0) return 0f;
                    float perf = (float)(samplePos / samplesPerBeat);
                    return timeMap != null ? timeMap.Ticks(perf) / 4f : perf;
                }
            }
        }

        /// <summary>Raised on the main thread when a non-looping playback reaches the end.</summary>
        public event Action Finished;

        /// <summary>Raised on the main thread for each melody note that started (staff, midi key, seconds).</summary>
        public event Action<int, int, float> NoteStarted;

        private struct StartedNote { public int staff, key; public float seconds; }
        private readonly List<StartedNote> startedNotes = new List<StartedNote>();
        private readonly List<StartedNote> startedScratch = new List<StartedNote>();

        private const int PreviewChannel = 15;

        private struct Ev
        {
            public long sample;
            public int channel;
            public int key;
            public int velocity;
            public bool on;
            public int staff;      // -1 = backing
            public float seconds;  // note length (for musicians)
        }

        private readonly object gate = new object();
        private Synthesizer synth;
        private Score score;

        private List<Ev> events = new List<Ev>();
        private int evIndex;
        private long samplePos;
        private long endSample;
        private double samplesPerBeat;
        private TimeMap timeMap;        // score ticks <-> performed beats (fermatas)
        private volatile bool playing;
        private volatile bool finishedFlag;
        private int[] channelPrograms = new int[16];

        private readonly List<KeyValuePair<long, int>> previewOffs = new List<KeyValuePair<long, int>>();
        private long clock;

        private float[] left = new float[0];
        private float[] right = new float[0];
        private int sampleRate;
        private AudioSource source;

        public static int ChannelForStaff(int staffIndex) { return staffIndex < 9 ? staffIndex : staffIndex + 1; } // skip drums (9)

        private void Awake()
        {
            sampleRate = AudioSettings.outputSampleRate;
            source = GetComponent<AudioSource>();

            // Dummy looping clip so Unity keeps the AudioSource alive and calls OnAudioFilterRead.
            var clip = AudioClip.Create("synth-dummy", 1, 1, sampleRate, false);
            clip.SetData(new float[] { 1f }, 0);
            source.clip = clip;
            source.loop = true;
            source.spatialBlend = 0f;
            source.playOnAwake = false;
        }

        public void Bind(Score s)
        {
            if (score != null) score.Changed -= OnScoreChanged;
            score = s;
            if (score != null) score.Changed += OnScoreChanged;
        }

        private IEnumerator Start()
        {
            var req = Resources.LoadAsync<TextAsset>(soundFontResource);
            yield return req;
            var asset = req.asset as TextAsset;
            if (asset == null)
            {
                LastError = "SoundFont not found: Resources/" + soundFontResource + ".bytes";
                Debug.LogError("[VRComposer] " + LastError);
                yield break;
            }

            byte[] bytes = asset.bytes;
            Resources.UnloadAsset(asset);
            int sr = sampleRate;
            int poly = Mathf.Clamp(maximumPolyphony, 8, 256);

            // Parsing ~30 MB takes a moment: do it off the main thread so the headset keeps its frame rate.
            var task = Task.Run(() =>
            {
                using (var ms = new MemoryStream(bytes))
                {
                    var font = new SoundFont(ms);
                    var settings = new SynthesizerSettings(sr);
                    settings.MaximumPolyphony = poly;
                    return new Synthesizer(font, settings);
                }
            });
            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                LastError = "SoundFont load failed: " + task.Exception.GetBaseException().Message;
                Debug.LogError("[VRComposer] " + LastError);
                yield break;
            }

            lock (gate)
            {
                synth = task.Result;
                for (int ch = 0; ch < 16; ch++) channelPrograms[ch] = -1;
                // Instruments are balanced with channel volume around Instruments.BaseVolume (85)
                // instead of the GM default 100, so quiet ones (piano) can be raised. The master
                // volume makes up the difference so the overall level is unchanged.
                float k = 100f / Instruments.BaseVolume;
                synth.MasterVolume *= k * k;
                for (int ch = 0; ch < 16; ch++) synth.ProcessMidiMessage(ch, 0xB0, 7, Instruments.BaseVolume);
            }
            IsReady = true;
            source.Play();
        }

        // ---------- transport ----------

        public void Play()
        {
            if (!IsReady || score == null) return;
            var built = BuildEvents(out long end, out double spb, out TimeMap map);
            lock (gate)
            {
                events = built;
                endSample = end;
                samplesPerBeat = spb;
                timeMap = map;
                samplePos = 0;
                evIndex = 0;
                ApplyProgramsLocked();
                synth.NoteOffAll(false);
                finishedFlag = false;
                playing = true;
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                playing = false;
                samplePos = 0;
                evIndex = 0;
                if (synth != null) synth.NoteOffAll(false);
            }
        }

        public void TogglePlay()
        {
            if (playing) Stop(); else Play();
        }

        /// <summary>Plays one short note so the user hears the pitch they just placed.</summary>
        public void PreviewNote(int program, int key, float seconds = 0.45f)
        {
            lock (gate)
            {
                if (synth == null) return;
                if (channelPrograms[PreviewChannel] != program)
                {
                    synth.ProcessMidiMessage(PreviewChannel, 0xC0, program, 0);
                    synth.ProcessMidiMessage(PreviewChannel, 0xB0, 91, reverb);
                    synth.ProcessMidiMessage(PreviewChannel, 0xB0, 7, Instruments.VolumeFor(program));
                    channelPrograms[PreviewChannel] = program;
                }
                synth.NoteOn(PreviewChannel, key, 90);
                previewOffs.Add(new KeyValuePair<long, int>(clock + (long)(seconds * sampleRate), key));
            }
        }

        /// <summary>Plays a chord block briefly (chord lane preview).</summary>
        public void PreviewChord(int[] keys, float seconds = 0.8f)
        {
            lock (gate)
            {
                if (synth == null) return;
                SetProgramLocked(ChordChannel, chordProgram);
                foreach (var k in keys)
                {
                    synth.NoteOn(ChordChannel, k, 70);
                    previewOffs.Add(new KeyValuePair<long, int>(clock + (long)(seconds * sampleRate), k + 1000 * ChordChannel));
                }
            }
        }

        // ---------- score -> events ----------

        public const int ChordChannel = 11, BassChannel = 12, DrumChannel = 9;
        public int chordProgram = 48;   // String Ensemble 1
        public int bassProgram = 32;    // Acoustic Bass

        private void OnScoreChanged()
        {
            if (!playing) return;

            // Live edit while playing: rebuild, keep the musical position.
            var built = BuildEvents(out long end, out double spb, out TimeMap map);
            lock (gate)
            {
                // resume at the same place in the score (ticks), even if fermatas moved
                double beat = samplesPerBeat > 0 ? samplePos / samplesPerBeat : 0;
                double tick = timeMap != null ? timeMap.Ticks((float)beat) : beat * 4;
                events = built;
                endSample = end;
                samplesPerBeat = spb;
                timeMap = map;
                samplePos = (long)(map.Beats((float)tick) * spb);
                if (samplePos >= endSample) samplePos = 0;
                evIndex = 0;
                while (evIndex < events.Count && events[evIndex].sample < samplePos) evIndex++;
                synth.NoteOffAll(false); // a deleted note must not keep ringing
                ApplyProgramsLocked();
            }
        }

        /// <summary>Adds a note-on/off pair. Times are score ticks, mapped through the fermata map.</summary>
        private void Add(List<Ev> list, double spb, TimeMap map, float startTick, float endTick, int ch, int key, int vel, int staff)
        {
            if (key < 0 || key > 127 || endTick <= startTick) return;
            double a = map.Beats(startTick) * spb, b = map.Beats(endTick) * spb;
            list.Add(new Ev { sample = (long)a, channel = ch, key = key, velocity = vel, on = true, staff = staff, seconds = (float)((b - a) / sampleRate) });
            list.Add(new Ev { sample = (long)b, channel = ch, key = key, on = false, staff = staff });
        }

        private List<Ev> BuildEvents(out long end, out double spb, out TimeMap map)
        {
            spb = sampleRate * 60.0 / Mathf.Max(1, score.tempoBpm);   // samples per quarter-note beat
            int totalTicks = score.TotalTicks;
            map = new TimeMap(score);
            end = (long)(map.TotalBeats * spb);

            var list = new List<Ev>();
            // ties, carried dynamics, cresc., sf, staccato, accents: see Performance.Realize
            foreach (var p in Performance.Realize(score))
                Add(list, spb, map, p.startTick, p.endTick, ChannelForStaff(p.staff), p.midi, p.velocity, p.staff);

            if (score.backingChords)
            {
                var k = score.Key;
                foreach (var c in score.chords)
                {
                    if (c.tick >= totalTicks) continue;
                    int bass;
                    var tones = BeginnerKit.ChordMidi(k, c.degree, out bass);
                    float a = c.tick, b = Mathf.Min(c.End, totalTicks);
                    foreach (var t in tones) Add(list, spb, map, a, b - 0.12f, ChordChannel, t, 52, -1);
                    // bass: root on every half bar (beats 1 and 3 in 4/4)
                    int half = Mathf.Max(4, score.TicksPerBar / 2);
                    for (int t = c.tick; t < Mathf.Min(c.End, totalTicks); t += half)
                        Add(list, spb, map, t, Mathf.Min(t + half, c.End) - 0.32f, BassChannel, bass, 80, -1);
                }
            }

            if (score.backingDrums)
            {
                int tpb = score.TicksPerBar;
                bool compound = score.timeSigDenominator == 8;
                for (int bar = 0; bar < score.bars; bar++)
                {
                    int bt = bar * tpb;
                    for (int t = 0; t < tpb; t += 2)
                    {
                        float tk = bt + t;
                        Add(list, spb, map, tk, tk + 0.4f, DrumChannel, 42, t % 4 == 0 ? 70 : 50, -1);          // closed hi-hat
                        bool kick = t == 0 || (!compound && tpb >= 16 && t == tpb / 2);
                        bool snare = compound ? t == tpb / 2 : (t % 8 == 4);
                        if (kick) Add(list, spb, map, tk, tk + 0.8f, DrumChannel, 36, 100, -1);
                        if (snare) Add(list, spb, map, tk, tk + 0.8f, DrumChannel, 38, 85, -1);
                    }
                }
            }

            // Time order; note-offs before note-ons at the same instant.
            list.Sort((a, b) => a.sample != b.sample ? a.sample.CompareTo(b.sample) : a.on.CompareTo(b.on));
            return list;
        }

        private void ApplyProgramsLocked()
        {
            if (synth == null || score == null) return;
            for (int s = 0; s < score.staves.Count; s++)
            {
                int ch = ChannelForStaff(s);
                int program = score.staves[s].program;
                if (channelPrograms[ch] == program) continue;
                synth.ProcessMidiMessage(ch, 0xC0, program, 0);
                synth.ProcessMidiMessage(ch, 0xB0, 91, reverb);
                synth.ProcessMidiMessage(ch, 0xB0, 7, Instruments.VolumeFor(program));   // loudness balance
                channelPrograms[ch] = program;
            }
            SetProgramLocked(ChordChannel, chordProgram);
            SetProgramLocked(BassChannel, bassProgram);
        }

        private void SetProgramLocked(int ch, int program)
        {
            if (channelPrograms[ch] == program) return;
            synth.ProcessMidiMessage(ch, 0xC0, program, 0);
            synth.ProcessMidiMessage(ch, 0xB0, 91, reverb);
            channelPrograms[ch] = program;
        }

        private void Update()
        {
            lock (gate)
            {
                startedScratch.Clear();
                startedScratch.AddRange(startedNotes);
                startedNotes.Clear();
            }
            if (NoteStarted != null) foreach (var n in startedScratch) NoteStarted(n.staff, n.key, n.seconds);

            if (finishedFlag)
            {
                finishedFlag = false;
                if (Finished != null) Finished();
            }
        }

        // ---------- audio thread ----------

        private void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / channels;
            if (left.Length < frames)
            {
                left = new float[frames];
                right = new float[frames];
            }

            lock (gate)
            {
                if (synth == null)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }

                for (int i = previewOffs.Count - 1; i >= 0; i--)
                {
                    if (previewOffs[i].Key <= clock)
                    {
                        int pv = previewOffs[i].Value;
                        synth.NoteOff(pv >= 1000 ? pv / 1000 : PreviewChannel, pv % 1000);
                        previewOffs.RemoveAt(i);
                    }
                }

                int done = 0;
                int guard = 0;
                while (done < frames && guard++ < 10000)
                {
                    int chunk = frames - done;

                    if (playing)
                    {
                        while (evIndex < events.Count && events[evIndex].sample <= samplePos)
                        {
                            var e = events[evIndex++];
                            if (e.on)
                            {
                                synth.NoteOn(e.channel, e.key, e.velocity);
                                if (e.staff >= 0 && startedNotes.Count < 64) startedNotes.Add(new StartedNote { staff = e.staff, key = e.key, seconds = e.seconds });
                            }
                            else synth.NoteOff(e.channel, e.key);
                        }

                        if (samplePos >= endSample)
                        {
                            if (loop && endSample > 0)
                            {
                                synth.NoteOffAll(false);
                                samplePos = 0;
                                evIndex = 0;
                                continue;
                            }
                            playing = false;
                            samplePos = 0;
                            evIndex = 0;
                            synth.NoteOffAll(false);
                            finishedFlag = true;
                        }
                        else
                        {
                            long next = evIndex < events.Count ? Math.Min(events[evIndex].sample, endSample) : endSample;
                            chunk = (int)Math.Max(1, Math.Min(chunk, next - samplePos));
                        }
                    }

                    synth.Render(new Span<float>(left, done, chunk), new Span<float>(right, done, chunk));
                    done += chunk;
                    if (playing) samplePos += chunk;
                }
                clock += frames;
            }

            float v = volume;
            for (int i = 0; i < frames; i++)
            {
                int o = i * channels;
                data[o] = left[i] * v;
                if (channels > 1) data[o + 1] = right[i] * v;
                for (int c = 2; c < channels; c++) data[o + c] = 0f;
            }
        }

        private void OnDestroy()
        {
            if (score != null) score.Changed -= OnScoreChanged;
            lock (gate) { synth = null; }
        }
    }
}
