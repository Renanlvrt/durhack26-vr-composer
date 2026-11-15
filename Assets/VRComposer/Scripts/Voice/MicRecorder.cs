using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace VRComposer.Voice
{
    /// <summary>
    /// The one owner of the microphone. Runs a looping mic clip and hands out 16 kHz mono chunks with
    /// their absolute sample index, so singing takes and the ElevenLabs agent read the same stream
    /// and every sample has an exact time.
    /// </summary>
    public class MicRecorder : MonoBehaviour
    {
        public const int Rate = 16000;

        [Tooltip("Empty = automatic: the headset mic (Oculus / Quest) if there is one, else the system default. Use the Mic button (or this field) to pick another one.")]
        public string deviceName = "";

        /// <summary>The mic in use (null = system default).</summary>
        public string CurrentDevice { get { return device; } }
        public event Action<string> DeviceChanged;

        /// <summary>(16 kHz mono samples, absolute index of the first one).</summary>
        public event Action<float[], long> Chunk;

        public long TotalSamples { get; private set; }
        public bool IsOn { get { return clip != null; } }
        /// <summary>Loudness of the last chunk, dBFS.</summary>
        public float LevelDb { get; private set; } = -120f;

        private AudioClip clip;
        private string device;
        private int readPos;
        private int devRate;
        private double resamplePos;   // fractional read position for linear resampling
        private float lastSample;

        public bool Begin()
        {
            if (clip != null) return true;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Permission.RequestUserPermission(Permission.Microphone);
                return false;
            }
#endif
            if (Microphone.devices.Length == 0) { Debug.LogWarning("[VRComposer] No microphone found."); return false; }
            Debug.Log("[VRComposer] Microphones: " + string.Join(" | ", Microphone.devices));
            device = string.IsNullOrEmpty(deviceName) ? PreferredDevice() : deviceName;
            if (device != null && Array.IndexOf(Microphone.devices, device) < 0)
            {
                // partial match ("Headset" / "Oculus")
                device = null;
                foreach (var d in Microphone.devices)
                    if (d.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0) { device = d; break; }
            }
            int min, max;
            Microphone.GetDeviceCaps(device, out min, out max);
            devRate = (min == 0 && max == 0) ? Rate : Mathf.Clamp(Rate, min, max);
            clip = Microphone.Start(device, true, 10, devRate);
            if (clip == null) { Debug.LogWarning("[VRComposer] Microphone.Start failed."); return false; }
            devRate = clip.frequency;
            readPos = 0;
            resamplePos = 0;
            DeviceChanged?.Invoke(device);
            Debug.Log("[VRComposer] Mic on: " + (device ?? "default") + " at " + devRate + " Hz");
            return true;
        }

        /// <summary>The headset's mic when it is connected (Quest Link / Air Link), else the default.</summary>
        private static string PreferredDevice()
        {
            foreach (var key in new[] { "Oculus", "Quest", "Headset" })
                foreach (var d in Microphone.devices)
                    if (d.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }

        /// <summary>Switches to the next microphone in the list (wraps around).</summary>
        public void NextDevice()
        {
            var all = Microphone.devices;
            if (all.Length == 0) return;
            int i = device == null ? -1 : Array.IndexOf(all, device);
            if (i < 0 && device == null) i = Array.IndexOf(all, PreferredDevice() ?? "");
            End();
            deviceName = all[(i + 1 + all.Length) % all.Length];
            Begin();
        }

        public static string ShortName(string d)
        {
            if (string.IsNullOrEmpty(d)) return "default";
            if (d.IndexOf("Oculus", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0) return "Headset";
            int p = d.IndexOf('(');
            string s = (p > 0 ? d.Substring(0, p) : d).Trim();
            return s.Length > 16 ? s.Substring(0, 16) : s;
        }

        public void End()
        {
            if (clip == null) return;
            Microphone.End(device);
            clip = null;
        }

        private void OnDestroy() { End(); }

        private void Update()
        {
            if (clip == null) return;
            int pos = Microphone.GetPosition(device);
            if (pos < 0 || pos == readPos) return;
            int n = pos - readPos;
            if (n < 0) n += clip.samples;
            var raw = new float[n * clip.channels];
            clip.GetData(raw, readPos);    // wraps around the looping clip
            readPos = pos;

            // mono
            int ch = clip.channels;
            var mono = ch == 1 ? raw : new float[n];
            if (ch > 1)
                for (int i = 0; i < n; i++)
                {
                    float s = 0;
                    for (int c = 0; c < ch; c++) s += raw[i * ch + c];
                    mono[i] = s / ch;
                }

            float[] outBuf = devRate == Rate ? mono : Resample(mono);
            if (outBuf.Length == 0) return;
            double e = 0;
            foreach (var s in outBuf) e += s * s;
            LevelDb = (float)(10 * Math.Log10(e / outBuf.Length + 1e-12));

            // Note: no automatic mic switching. The Quest headset mic outputs digital silence both when
            // the headset is not active and between phrases (its noise gate), so "silent" does not mean
            // "wrong mic". The headset mic is preferred at start; the Mic button switches by hand.
            long start = TotalSamples;
            TotalSamples += outBuf.Length;
            Chunk?.Invoke(outBuf, start);
        }

        private float[] Resample(float[] src)
        {
            double step = (double)devRate / Rate;
            var list = new System.Collections.Generic.List<float>((int)((long)src.Length * Rate / devRate) + 2);   // long: no overflow on big chunks
            // resamplePos is relative to the previous chunk's last sample (index -1 = lastSample)
            while (resamplePos < src.Length - 1)
            {
                int i = (int)Math.Floor(resamplePos);
                double f = resamplePos - i;
                float a = i < 0 ? lastSample : src[i];
                float b = src[i + 1];
                list.Add((float)(a + (b - a) * f));
                resamplePos += step;
            }
            resamplePos -= src.Length;
            lastSample = src[src.Length - 1];
            return list.ToArray();
        }

        /// <summary>Writes 16 kHz mono float samples as a 16-bit WAV.</summary>
        public static void SaveWav(string path, float[] samples, int rate = Rate)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                int bytes = samples.Length * 2;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                foreach (var s in samples) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(s * 32767f), -32768, 32767));
            }
        }

        /// <summary>Reads a 16-bit PCM WAV (any rate, mono or stereo) and returns 16 kHz mono.</summary>
        public static float[] LoadWav16k(string path)
        {
            var b = System.IO.File.ReadAllBytes(path);
            int ch = BitConverter.ToInt16(b, 22), rate = BitConverter.ToInt32(b, 24), bits = BitConverter.ToInt16(b, 34);
            int p = 12, data = -1, len = 0;
            while (p + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                if (id == "data") { data = p + 8; len = Math.Min(size, b.Length - data); break; }
                p += 8 + size + (size & 1);
            }
            if (data < 0 || bits != 16) throw new Exception("Only 16-bit PCM WAV files are supported.");
            int frames = len / (2 * ch);
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += BitConverter.ToInt16(b, data + (i * ch + c) * 2) / 32768f;
                mono[i] = s / ch;
            }
            if (rate == Rate) return mono;
            int n = (int)((long)frames * Rate / rate);
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                double x = (double)i * rate / Rate;
                int a = (int)x; double f = x - a;
                o[i] = (float)(mono[Math.Min(a, frames - 1)] * (1 - f) + mono[Math.Min(a + 1, frames - 1)] * f);
            }
            return o;
        }
    }
}
