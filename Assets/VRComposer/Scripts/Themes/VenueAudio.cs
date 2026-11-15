using System.Collections.Generic;
using UnityEngine;

namespace VRComposer
{
    public enum VenueId { Studio, GoldenHall, RecitalRoom, JazzClub, Cathedral }

    /// Room acoustics, atmosphere, applause and tour narration.
    /// Real recordings (ElevenLabs) are loaded from Resources/VRComposerAudio if present;
    /// otherwise convincing stand-ins are synthesised so the experience is never silent.
    public class VenueAudio : MonoBehaviour
    {
        /// Background crowd/room sound and the end-of-piece applause. Both are off: they were too loud,
        /// and synthesising them on the main thread (16 s and 8 s of audio) froze the headset for a
        /// moment on every venue switch and at the end of each piece. The code is kept: set to true
        /// (VR Composer object > Venue Ambience / Applause) to bring them back.
        public static bool EnableAmbience = false;
        public static bool EnableApplause = false;

        AudioReverbFilter reverb;
        AudioSource ambience, applause, narrator;
        readonly Dictionary<string, AudioClip> generated = new Dictionary<string, AudioClip>();
        VenueId current = VenueId.Studio;
        public float NarrationRemaining => narrator != null && narrator.isPlaying && narrator.clip != null ? narrator.clip.length - narrator.time : 0f;

        public void Init(AudioListener listener)
        {
            // Reverb on the listener colours everything in the room: the music, the crowd and the applause.
            reverb = listener.gameObject.AddComponent<AudioReverbFilter>();
            reverb.reverbPreset = AudioReverbPreset.Room;
            // Each source lives on its own child object. This GameObject also holds the ScorePlayer,
            // whose OnAudioFilterRead synth must be the only AudioSource here: with several, Unity
            // logs an error on every audio callback and runs the synth once per source.
            ambience = NewSource("Venue Ambience");
            ambience.loop = true; ambience.playOnAwake = false; ambience.spatialBlend = 0f;
            applause = NewSource("Venue Applause");
            applause.playOnAwake = false; applause.spatialBlend = 0f;
            narrator = NewSource("Venue Narrator");
            narrator.playOnAwake = false; narrator.spatialBlend = 0f;
            narrator.bypassListenerEffects = true;   // the guide's voice stays dry and close
            narrator.priority = 0;
        }

        AudioSource NewSource(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.AddComponent<AudioSource>();
        }

        static string Key(VenueId v) => v switch
        {
            VenueId.GoldenHall => "hall", VenueId.RecitalRoom => "recital", VenueId.JazzClub => "jazz", VenueId.Cathedral => "cathedral", _ => "studio"
        };

        static AudioClip Load(string name) => Resources.Load<AudioClip>("VRComposerAudio/" + name);

        public void SetVenue(VenueId v)
        {
            current = v;
            reverb.reverbPreset = v switch
            {
                VenueId.GoldenHall => AudioReverbPreset.Concerthall,   // big, warm, ~4 s tail
                VenueId.RecitalRoom => AudioReverbPreset.Auditorium,
                VenueId.JazzClub => AudioReverbPreset.Livingroom,      // close, dry, intimate
                VenueId.Cathedral => AudioReverbPreset.Arena,          // very long stone echo
                _ => AudioReverbPreset.Room,
            };
            ambience.Stop();
            if (v == VenueId.Studio || !EnableAmbience) return;
            var clip = Load("ambience_" + Key(v)) ?? Generated("amb_" + Key(v), () => SynthAmbience(v));
            ambience.clip = clip;
            ambience.volume = v switch { VenueId.JazzClub => 0.5f, VenueId.GoldenHall => 0.35f, VenueId.Cathedral => 0.4f, _ => 0.35f };
            ambience.Play();
        }

        /// Plays applause for the current venue and returns how long it lasts.
        public float PlayApplause()
        {
            if (!EnableApplause) return 0f;
            var clip = Load("applause_" + Key(current)) ?? Generated("app_" + Key(current), () => SynthApplause(current));
            applause.clip = clip;
            applause.volume = current == VenueId.JazzClub ? 0.8f : 0.9f;
            applause.Play();
            // let the crowd murmur dip under the applause
            if (ambience.isPlaying) ambience.volume *= 0.6f;
            return clip.length;
        }

        public bool ApplausePlaying => applause != null && applause.isPlaying;

        /// Plays tour narration line i. Returns its length, or 0 if the recording isn't in the project yet.
        public float PlayNarration(int i)
        {
            narrator.Stop();
            var clip = Load("tour_" + i);
            if (clip == null) return 0f;
            narrator.clip = clip; narrator.volume = 1f; narrator.Play();
            return clip.length;
        }

        public void StopNarration() { if (narrator != null) narrator.Stop(); }

        AudioClip Generated(string key, System.Func<AudioClip> make)
        {
            if (!generated.TryGetValue(key, out var c) || c == null) { c = make(); generated[key] = c; }
            return c;
        }

        // ---------------- synthesised stand-ins ----------------
        const int SR = 22050;
        static System.Random rng = new System.Random(3);
        static float R() => (float)rng.NextDouble();

        /// Hundreds of individual hand claps: each a short band-limited noise burst, at each person's own tempo.
        static AudioClip SynthApplause(VenueId v)
        {
            int people = v switch { VenueId.GoldenHall => 260, VenueId.Cathedral => 140, VenueId.RecitalRoom => 90, _ => 45 };
            float secs = v == VenueId.JazzClub ? 6f : 8f;
            int n = (int)(secs * SR);
            var d = new float[n];
            // a few clap "shapes"
            var claps = new float[6][];
            for (int k = 0; k < claps.Length; k++)
            {
                int len = (int)(SR * (0.012f + 0.01f * R()));
                var c = new float[len]; float lp = 0, lp2 = 0; float bright = 0.35f + 0.4f * R();
                for (int i = 0; i < len; i++)
                {
                    float x = R() * 2 - 1;
                    lp += bright * (x - lp); lp2 += 0.08f * (lp - lp2);
                    c[i] = (lp - lp2) * Mathf.Exp(-i / (len * 0.25f));
                }
                claps[k] = c;
            }
            for (int p = 0; p < people; p++)
            {
                float rate = 3.2f + 2.4f * R();                       // claps per second
                float t = 0.15f + 0.9f * R() * R();                   // people join in over the first second
                float stop = secs - 0.3f - 2.8f * R();                // and drop out at different times
                float amp = 0.05f + 0.07f * R();
                while (t < stop)
                {
                    var c = claps[rng.Next(claps.Length)];
                    int s0 = (int)(t * SR);
                    float fade = Mathf.Clamp01((stop - t) / 1.5f);
                    for (int i = 0; i < c.Length && s0 + i < n; i++) d[s0 + i] += c[i] * amp * fade;
                    t += (1f / rate) * (0.85f + 0.3f * R());
                }
            }
            if (v == VenueId.JazzClub) AddWhoops(d, 3);
            Normalise(d, 0.8f);
            var clip = AudioClip.Create("applause_" + v, n, 1, SR, false);
            clip.SetData(d, 0);
            return clip;
        }

        static void AddWhoops(float[] d, int count)
        {
            for (int w = 0; w < count; w++)
            {
                int s0 = (int)((0.4f + 2.5f * R()) * SR), len = (int)(0.45f * SR);
                float f0 = 900 + 500 * R(), ph = 0;
                for (int i = 0; i < len && s0 + i < d.Length; i++)
                {
                    float t = i / (float)len;
                    float f = f0 * (1f + 0.6f * Mathf.Sin(t * Mathf.PI));      // rising-falling "woo"
                    ph += 2 * Mathf.PI * f / SR;
                    d[s0 + i] += 0.08f * Mathf.Sin(ph) * Mathf.Sin(t * Mathf.PI);
                }
            }
        }

        static AudioClip SynthAmbience(VenueId v)
        {
            float secs = 16f;
            int n = (int)(secs * SR);
            var d = new float[n];
            // room tone: soft brown noise
            float brown = 0;
            float tone = v == VenueId.Cathedral ? 0.06f : v == VenueId.GoldenHall ? 0.03f : 0.04f;
            for (int i = 0; i < n; i++) { brown = Mathf.Clamp(brown + (R() * 2 - 1) * 0.02f, -1, 1); d[i] = brown * tone; }

            if (v == VenueId.JazzClub)
            {
                // faint chatter: several "voices" of band-limited noise with syllable-like rhythms
                for (int voice = 0; voice < 9; voice++)
                {
                    float lp = 0, hp = 0, syll = 2.5f + 3f * R(), ph = R() * 10, amp = 0.05f + 0.05f * R(), bright = 0.12f + 0.1f * R();
                    float talkPhase = R() * 6;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)SR;
                        float talking = Mathf.Clamp01(Mathf.Sin((t + talkPhase) * 0.45f) * 2f + 0.3f);     // people pause between sentences
                        float env = Mathf.Pow(Mathf.Abs(Mathf.Sin((t + ph) * syll * Mathf.PI)), 2f) * talking;
                        float x = R() * 2 - 1;
                        lp += bright * (x - lp); hp += 0.02f * (lp - hp);
                        d[i] += (lp - hp) * env * amp;
                    }
                }
                // glasses and cutlery clinks
                for (int k = 0; k < 10; k++)
                {
                    int s0 = (int)(R() * (n - SR)); float f = 2500 + 2500 * R(), ph = 0;
                    for (int i = 0; i < SR / 3; i++) { ph += 2 * Mathf.PI * f / SR; d[s0 + i] += 0.05f * Mathf.Sin(ph) * Mathf.Exp(-i / (SR * 0.05f)); }
                }
            }
            else if (v != VenueId.Studio)
            {
                // an occasional distant cough or seat creak, very quiet
                for (int k = 0; k < 3; k++)
                {
                    int s0 = (int)(R() * (n - SR)); float lp = 0;
                    for (int i = 0; i < SR / 4; i++) { lp += 0.15f * ((R() * 2 - 1) - lp); d[s0 + i] += lp * 0.08f * Mathf.Exp(-i / (SR * 0.06f)); }
                }
            }
            // seamless loop: crossfade the last second into the first
            int xf = SR;
            for (int i = 0; i < xf; i++) { float a = i / (float)xf; d[i] = d[i] * a + d[n - xf + i] * (1 - a); }
            System.Array.Resize(ref d, n - xf);
            Normalise(d, v == VenueId.JazzClub ? 0.5f : 0.2f);
            var clip = AudioClip.Create("ambience_" + v, d.Length, 1, SR, false);
            clip.SetData(d, 0);
            return clip;
        }

        static void Normalise(float[] d, float peak)
        {
            float m = 1e-6f; foreach (var x in d) m = Mathf.Max(m, Mathf.Abs(x));
            float k = peak / m; for (int i = 0; i < d.Length; i++) d[i] *= k;
        }
    }
}
