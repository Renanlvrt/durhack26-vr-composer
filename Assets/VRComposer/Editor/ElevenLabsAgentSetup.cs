using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VRComposer.Voice;

namespace VRComposer.EditorTools
{
    /// <summary>
    /// Tools > VR Composer > Create/Update ElevenLabs Agent: creates (or updates) the client tools and
    /// the agent described in ElevenLabsAgent.json (Claude as the LLM, 16 kHz PCM, private auth),
    /// and stores the agent id in the .env file. The API key is read from .env and never logged.
    /// </summary>
    public static class ElevenLabsAgentSetup
    {
        private const string Api = "https://api.elevenlabs.io";

        [MenuItem("Tools/VR Composer/Create or Update ElevenLabs Agent")]
        public static void Run()
        {
            string key = ElevenLabsConfig.ApiKey;
            if (string.IsNullOrEmpty(key))
            {
                EditorUtility.DisplayDialog("ElevenLabs", "Put ELEVENLABS_API_KEY=... in " + ElevenLabsConfig.EnvPath + " (that file is git-ignored).", "OK");
                return;
            }
            try
            {
                var cfgPath = Path.Combine(Application.dataPath, "VRComposer/Editor/ElevenLabsAgent.json");
                var cfg = JObject.Parse(File.ReadAllText(cfgPath));
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.Add("xi-api-key", key);
                    EditorUtility.DisplayProgressBar("ElevenLabs", "Tools...", 0.2f);

                    var existing = new Dictionary<string, string>();
                    var list = Call(http, "GET", "/v1/convai/tools", null);
                    foreach (var t in (JArray)list["tools"]) existing[(string)t["tool_config"]["name"]] = (string)t["id"];

                    var ids = new JArray();
                    foreach (JObject t in (JArray)cfg["tools"])
                    {
                        var tc = new JObject
                        {
                            ["type"] = "client",
                            ["name"] = t["name"],
                            ["description"] = t["description"],
                            ["expects_response"] = true,
                            ["response_timeout_secs"] = 20,
                            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = t["properties"], ["required"] = t["required"] },
                        };
                        string name = (string)t["name"], id;
                        if (existing.TryGetValue(name, out id)) Call(http, "PATCH", "/v1/convai/tools/" + id, new JObject { ["tool_config"] = tc });
                        else id = (string)Call(http, "POST", "/v1/convai/tools", new JObject { ["tool_config"] = tc })["id"];
                        ids.Add(id);
                    }

                    EditorUtility.DisplayProgressBar("ElevenLabs", "Agent...", 0.7f);
                    var body = new JObject
                    {
                        ["name"] = cfg["name"],
                        ["conversation_config"] = new JObject
                        {
                            ["agent"] = new JObject
                            {
                                ["first_message"] = "",
                                ["language"] = "en",
                                ["prompt"] = new JObject
                                {
                                    ["prompt"] = cfg["prompt"],
                                    ["llm"] = cfg["llm"],
                                    ["temperature"] = cfg["temperature"],
                                    ["reasoning_effort"] = cfg["reasoning_effort"],
                                    ["tool_ids"] = ids,
                                },
                            },
                            ["tts"] = new JObject { ["agent_output_audio_format"] = "pcm_16000" },
                            ["asr"] = new JObject { ["user_input_audio_format"] = "pcm_16000" },
                            ["turn"] = new JObject { ["turn_timeout"] = cfg["turn_timeout"] ?? 30 },
                        },
                        ["platform_settings"] = new JObject { ["auth"] = new JObject { ["enable_auth"] = true } },
                    };
                    string agent = ElevenLabsConfig.AgentId;
                    if (!string.IsNullOrEmpty(agent)) Call(http, "PATCH", "/v1/convai/agents/" + agent, body);
                    else
                    {
                        agent = (string)Call(http, "POST", "/v1/convai/agents/create", body)["agent_id"];
                        ElevenLabsConfig.Set(ElevenLabsConfig.AgentVar, agent);
                    }
                    Debug.Log("[VRComposer] ElevenLabs agent ready: " + agent + " (" + cfg["llm"] + ", " + ids.Count + " tools)");
                    EditorUtility.DisplayDialog("ElevenLabs", "Agent ready (" + cfg["llm"] + ").\nHold Y (or T) in Play mode and ask: \"Suggest the next part\".", "OK");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[VRComposer] ElevenLabs setup failed: " + e.Message);
                EditorUtility.DisplayDialog("ElevenLabs", "Setup failed:\n" + e.Message, "OK");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static JObject Call(HttpClient http, string method, string path, JObject body)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), Api + path);
            if (body != null) req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            var resp = http.SendAsync(req).Result;
            var text = resp.Content.ReadAsStringAsync().Result;
            if (!resp.IsSuccessStatusCode) throw new Exception(method + " " + path + ": " + (int)resp.StatusCode + " " + (text.Length > 400 ? text.Substring(0, 400) : text));
            return string.IsNullOrEmpty(text) ? new JObject() : JObject.Parse(text);
        }

        [MenuItem("Tools/VR Composer/Transcribe WAV Take...")]
        public static void TranscribeWav()
        {
            string dir = Path.Combine(Application.persistentDataPath, "takes");
            string path = EditorUtility.OpenFilePanel("Sung take (16-bit WAV)", Directory.Exists(dir) ? dir : Application.dataPath, "wav");
            if (string.IsNullOrEmpty(path)) return;
            double bpm = 96; int tpb = 16;
            var meta = Path.ChangeExtension(path, ".json");
            if (File.Exists(meta))
            {
                var m = JObject.Parse(File.ReadAllText(meta));
                bpm = (double?)m["bpm"] ?? bpm; tpb = (int?)m["ticksPerBar"] ?? tpb;
            }
            var audio = MicRecorder.LoadWav16k(path);
            var frames = new YinTracker().Track(audio, MicRecorder.Rate);
            double tuning;
            var notes = NoteSegmenter.Segment(frames, new SegmenterSettings(), out tuning);
            // the first sung note is beat 1 (as in the app)
            if (notes.Count > 0) { double first = notes[0].start; foreach (var n in notes) { n.start -= first; n.end -= first; } }
            var ticks = RhythmQuantizer.Quantize(notes, bpm, tpb, 1, true);
            var sb = new StringBuilder();
            sb.AppendLine("[VRComposer] " + Path.GetFileName(path) + ": " + notes.Count + " notes, tuning " + Math.Round(tuning) + " cents, " + bpm + " bpm");
            foreach (var n in notes) sb.AppendLine("  " + n + "  " + Score.MidiName(n.midi, false));
            sb.AppendLine("  quantised:");
            foreach (var t in ticks) sb.AppendLine("    " + (t.midi < 0 ? "rest" : Score.MidiName(t.midi, false)) + " bar " + (t.tick / tpb + 1) + " +" + (t.tick % tpb) + " len " + t.length + (t.tieToNext ? " ~" : ""));
            Debug.Log(sb.ToString());
        }
    }
}
