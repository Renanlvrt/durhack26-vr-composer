using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VRComposer.Voice
{
    /// <summary>
    /// ElevenLabs Agents (Conversational AI) client over WebSocket. The agent's LLM is Claude; it
    /// hears you only while you hold Y / T (push-to-talk), answers in a short spoken line, and
    /// writes music through client tools (AgentTools), never by speaking notes.
    /// </summary>
    public class ElevenAgent : MonoBehaviour
    {
        public event Action<string> StatusChanged;
        public bool Talking { get; private set; }
        public bool Speaking { get { lock (playLock) return playCount > 0; } }
        public bool Connected { get { return ws != null && ws.State == WebSocketState.Open && ready; } }

        private ClientWebSocket ws;
        private CancellationTokenSource cts;
        private Task connecting;
        private volatile bool ready;
        private readonly ConcurrentQueue<string> inbox = new ConcurrentQueue<string>();
        private readonly BlockingCollection<string> outbox = new BlockingCollection<string>();
        private readonly List<string> pending = new List<string>();   // audio said before the socket was ready
        private MicRecorder mic;
        private readonly List<float> micAcc = new List<float>();
        private readonly Queue<float[]> preroll = new Queue<float[]>();   // last ~0.4 s, sent when talking starts
        private int prerollSamples;
        private float lastActivity;
        private bool closingOnPurpose;
        private int audioChunks;
        /// <summary>Agent audio chunks received so far (any thread).</summary>
        public int AudioChunks { get { return Interlocked.CompareExchange(ref audioChunks, 0, 0); } }
        /// <summary>Seconds since the agent's voice last played (main thread).</summary>
        public float SilentFor { get { return Time.unscaledTime - lastSpoke; } }
        private float lastSpoke = -100f;
        [Tooltip("Close the connection after this many idle seconds (reconnects on the next press).")]
        public float idleCloseSeconds = 20f;
        private string lastStatus;

        // playback ring (agent voice)
        private AudioSource source;
        private int outRate = 16000;
        private readonly object playLock = new object();
        private float[] ring = new float[16000 * 60];
        private int playRead, playWrite, playCount;

        private void Awake()
        {
            mic = GetComponent<MicRecorder>();
            if (mic == null) mic = gameObject.AddComponent<MicRecorder>();
            mic.Chunk += KeepPreroll;
        }

        private void KeepPreroll(float[] s, long start)
        {
            if (Talking) return;
            preroll.Enqueue(s);
            prerollSamples += s.Length;
            while (preroll.Count > 1 && prerollSamples - preroll.Peek().Length >= MicRecorder.Rate * 4 / 10)
                prerollSamples -= preroll.Dequeue().Length;
        }

        /// <summary>Stops the agent's voice (a singing take is starting).</summary>
        public void Hush() { StopPlayback(); }

        private void SetStatus(string s)
        {
            if (s != lastStatus) Debug.Log("[VRComposer] Composer status: " + s);
            lastStatus = s;
            StatusChanged?.Invoke(s);
        }

        // what was said in this push-to-talk turn (for "didn't hear you" feedback)
        private float talkPeak, talkSeconds, releasedAt = -1f;
        private bool awaitingHeard;
        [Tooltip("Seconds after releasing Y without any transcript before telling the user nothing was heard.")]
        public float heardTimeoutSeconds = 8f;

        private void Measure(float[] s)
        {
            for (int i = 0; i < s.Length; i++) { float a = Math.Abs(s[i]); if (a > talkPeak) talkPeak = a; }
            talkSeconds += s.Length / (float)MicRecorder.Rate;
        }

        private static string Db(float peak) { return peak > 0 ? Mathf.RoundToInt(20f * Mathf.Log10(peak)) + " dB" : "silence"; }

        // ================================================================== push-to-talk

        public void PushToTalk(bool down)
        {
            if (down == Talking) return;
            if (down)
            {
                if (string.IsNullOrEmpty(ElevenLabsConfig.ApiKey)) { SetStatus("Add ELEVENLABS_API_KEY to the .env file"); return; }
                if (string.IsNullOrEmpty(ElevenLabsConfig.AgentId)) { SetStatus("Run Tools > VR Composer > Create/Update ElevenLabs Agent first"); return; }
                var vc = VoiceComposer.Instance;
                if (vc != null && vc.Busy) { SetStatus("Finish the singing take first"); return; }
                if (!mic.Begin()) { SetStatus("No microphone"); return; }
                StopPlayback();                       // talking over the composer interrupts it
                EnsureConnected();
                Talking = true;
                lastActivity = Time.unscaledTime;
                micAcc.Clear();
                talkPeak = 0f; talkSeconds = 0f; awaitingHeard = false;
                foreach (var p in preroll) { micAcc.AddRange(p); Measure(p); }      // the first syllable said while pressing
                preroll.Clear(); prerollSamples = 0;
                mic.Chunk += OnMic;
                SetStatus("Listening... (release Y when done)");
            }
            else
            {
                Talking = false;
                mic.Chunk -= OnMic;
                FlushMic();
                // trailing silence so the server's turn detection closes the turn right away
                var silence = new float[MicRecorder.Rate / 5];
                for (int i = 0; i < 4; i++) SendAudio(silence);
                lastActivity = Time.unscaledTime;
                releasedAt = Time.unscaledTime;
                awaitingHeard = true;
                Debug.Log("[VRComposer] Push-to-talk released: " + talkSeconds.ToString("0.0") + " s from " + MicRecorder.ShortName(mic.CurrentDevice) + ", peak " + Db(talkPeak) + (Connected ? "" : " (still connecting)"));
                if (talkPeak < 0.003f)   // about -50 dB: the mic gave (almost) nothing
                    SetStatus("Your mic sent " + Db(talkPeak) + ": tap the Mic button to switch, then hold Y again");
                else SetStatus("Thinking...");
            }
        }

        /// <summary>Typed request (desktop / noisy room).</summary>
        public void SendText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            EnsureConnected();
            Send(new JObject { ["type"] = "user_message", ["text"] = text }.ToString(Newtonsoft.Json.Formatting.None));
            SetStatus("You: " + text);
        }

        private void OnMic(float[] s, long start)
        {
            Measure(s);
            micAcc.AddRange(s);
            if (micAcc.Count >= MicRecorder.Rate / 10) FlushMic();   // 100 ms packets
        }

        private void FlushMic()
        {
            if (micAcc.Count == 0) return;
            SendAudio(micAcc.ToArray());
            micAcc.Clear();
        }

        private void SendAudio(float[] s)
        {
            var bytes = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                short v = (short)Mathf.Clamp(Mathf.RoundToInt(s[i] * 32767f), -32768, 32767);
                bytes[i * 2] = (byte)(v & 0xff);
                bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
            Send("{\"user_audio_chunk\":\"" + Convert.ToBase64String(bytes) + "\"}");
        }

        private void Send(string json)
        {
            if (Connected) outbox.Add(json);
            else lock (pending) { if (pending.Count < 200) pending.Add(json); }
        }

        // ================================================================== connection

        private void EnsureConnected()
        {
            if (Connected || (connecting != null && !connecting.IsCompleted)) return;
            ready = false;
            closingOnPurpose = false;
            lastActivity = Time.unscaledTime;
            cts?.Cancel();
            cts = new CancellationTokenSource();
            string key = ElevenLabsConfig.ApiKey, agent = ElevenLabsConfig.AgentId;
            SetStatus("Connecting to the composer...");
            connecting = Task.Run(() => Connect(key, agent, cts.Token));
        }

        private async Task Connect(string key, string agentId, CancellationToken ct)
        {
            try
            {
                string url;
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.Add("xi-api-key", key);
                    var resp = await http.GetAsync("https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=" + Uri.EscapeDataString(agentId), ct);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) throw new Exception("signed URL: " + (int)resp.StatusCode + " " + Trim(body));
                    url = (string)JObject.Parse(body)["signed_url"];
                }
                ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);
                var init = new JObject { ["type"] = "conversation_initiation_client_data" };
                await SendRaw(init.ToString(Newtonsoft.Json.Formatting.None), ct);
                ready = true;
                lock (pending) { foreach (var p in pending) outbox.Add(p); pending.Clear(); }
                _ = Task.Run(() => SendLoop(ct));
                await ReceiveLoop(ct);
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested) inbox.Enqueue("{\"type\":\"_error\",\"message\":" + Newtonsoft.Json.JsonConvert.ToString(e.Message) + "}");
            }
            finally { ready = false; }
        }

        private static string Trim(string s) { return s != null && s.Length > 300 ? s.Substring(0, 300) : s; }

        private async Task SendRaw(string json, CancellationToken ct)
        {
            var b = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, ct);
        }

        private async Task SendLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    string msg;
                    if (outbox.TryTake(out msg, 200, ct)) await SendRaw(msg, ct);
                }
            }
            catch (Exception) { }
        }

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[1 << 16];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    inbox.Enqueue("{\"type\":\"_closed\",\"message\":" + Newtonsoft.Json.JsonConvert.ToString(r.CloseStatusDescription ?? "") + "}");
                    break;
                }
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                string msg = sb.ToString();
                sb.Clear();
                // audio and pings are handled here so playback does not wait for a frame
                if (msg.Contains("\"type\":\"audio\"") || msg.Contains("\"type\": \"audio\"")) { HandleAudio(msg); continue; }
                if (msg.Contains("\"type\":\"ping\"") || msg.Contains("\"type\": \"ping\""))
                {
                    var p = JObject.Parse(msg);
                    outbox.Add(new JObject { ["type"] = "pong", ["event_id"] = p["ping_event"]?["event_id"] }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                inbox.Enqueue(msg);
            }
        }

        private void HandleAudio(string msg)
        {
            try
            {
                var b64 = (string)JObject.Parse(msg)["audio_event"]?["audio_base_64"];
                if (string.IsNullOrEmpty(b64)) return;
                var bytes = Convert.FromBase64String(b64);
                Interlocked.Increment(ref audioChunks);
                lock (playLock)
                {
                    for (int i = 0; i + 1 < bytes.Length; i += 2)
                    {
                        if (playCount >= ring.Length) break;
                        ring[playWrite] = (short)(bytes[i] | (bytes[i + 1] << 8)) / 32768f;
                        playWrite = (playWrite + 1) % ring.Length;
                        playCount++;
                    }
                }
            }
            catch (Exception) { }
        }

        // ================================================================== main thread

        private void Update()
        {
            string msg;
            while (inbox.TryDequeue(out msg)) { lastActivity = Time.unscaledTime; Handle(msg); }
            if (Speaking) { lastSpoke = Time.unscaledTime; lastActivity = Time.unscaledTime; }
            if (Talking) lastActivity = Time.unscaledTime;
            if (awaitingHeard && !Talking && Time.unscaledTime - releasedAt > heardTimeoutSeconds)
            {
                awaitingHeard = false;
                SetStatus(Connected
                    ? "The composer heard nothing (mic peak " + Db(talkPeak) + "). Hold Y, speak, then release"
                    : "Could not reach the composer (check the internet), hold Y to retry");
            }
            // an idle open conversation can re-engage on its own (and keeps a session running): close it
            if (Connected && !Talking && Time.unscaledTime - lastActivity > idleCloseSeconds) Close();
        }

        private void Close()
        {
            closingOnPurpose = true;
            ready = false;
            cts?.Cancel();
            var w = ws;
            ws = null;
            try { w?.Abort(); w?.Dispose(); } catch { }
            SetStatus("Composer: hold Y and ask");
        }

        private void Handle(string msg)
        {
            JObject j;
            try { j = JObject.Parse(msg); } catch { return; }
            string type = (string)j["type"];
            switch (type)
            {
                case "conversation_initiation_metadata":
                    var fmt = (string)j["conversation_initiation_metadata_event"]?["agent_output_audio_format"];
                    int rate;
                    if (fmt != null && fmt.StartsWith("pcm_") && int.TryParse(fmt.Substring(4), out rate) && rate != outRate) { outRate = rate; source = null; }
                    EnsureSource();
                    Debug.Log("[VRComposer] Composer connected (" + (fmt ?? "?") + ")");
                    if (!Talking && !awaitingHeard) SetStatus("Composer ready: hold Y and ask");
                    break;
                case "user_transcript":
                    var said = (string)j["user_transcription_event"]?["user_transcript"];
                    awaitingHeard = false;
                    if (string.IsNullOrWhiteSpace(said) || said.Trim() == "...") SetStatus("Didn't catch that (mic peak " + Db(talkPeak) + "). Hold Y and speak again");
                    else SetStatus("You: " + said);
                    break;
                case "agent_response":
                    var reply = (string)j["agent_response_event"]?["agent_response"];
                    if (!string.IsNullOrEmpty(reply)) SetStatus("Composer: " + reply);
                    break;
                case "interruption":
                    StopPlayback();
                    break;
                case "client_tool_call":
                    var call = j["client_tool_call"] as JObject;
                    if (call == null) break;
                    string tool = (string)call["tool_name"], id = (string)call["tool_call_id"];
                    var args = call["parameters"] as JObject ?? new JObject();
                    SetStatus("Composer: " + Friendly(tool) + "...");
                    var res = AgentTools.Handle(tool, args);
                    Debug.Log("[VRComposer] tool " + tool + " " + args.ToString(Newtonsoft.Json.Formatting.None) + " -> " + (res.error ? "ERROR " : "") + res.text);
                    outbox.Add(new JObject { ["type"] = "client_tool_result", ["tool_call_id"] = id, ["result"] = res.text, ["is_error"] = res.error }.ToString(Newtonsoft.Json.Formatting.None));
                    if (tool != "get_score") SetStatus(res.error ? "Composer is fixing: " + res.text : res.text);
                    break;
                case "_error":
                    awaitingHeard = false;
                    if (!closingOnPurpose) SetStatus("Composer offline: " + (string)j["message"]);
                    break;
                case "_closed":
                    awaitingHeard = false;
                    if (!closingOnPurpose) SetStatus("Composer disconnected" + (string.IsNullOrEmpty((string)j["message"]) ? "" : " (" + (string)j["message"] + ")") + ", hold Y to reconnect");
                    break;
            }
        }

        private static string Friendly(string tool)
        {
            switch (tool)
            {
                case "get_score": return "reading the score";
                case "write_bars": return "writing the next bars";
                case "write_part": return "writing a new part";
                default: return tool.Replace('_', ' ');
            }
        }

        // ================================================================== playback

        private void EnsureSource()
        {
            if (source != null) return;
            var go = new GameObject("Composer Voice");
            go.transform.SetParent(transform, false);
            source = go.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.loop = true;
            source.clip = AudioClip.Create("ComposerVoice", outRate * 2, 1, outRate, true, OnRead);
            source.Play();
        }

        private void OnRead(float[] data)
        {
            lock (playLock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (playCount > 0)
                    {
                        data[i] = ring[playRead];
                        playRead = (playRead + 1) % ring.Length;
                        playCount--;
                    }
                    else data[i] = 0f;
                }
            }
        }

        private void StopPlayback()
        {
            lock (playLock) { playCount = 0; playRead = playWrite; }
        }

        private void OnDestroy()
        {
            cts?.Cancel();
            try { ws?.Abort(); } catch { }
        }
    }
}
