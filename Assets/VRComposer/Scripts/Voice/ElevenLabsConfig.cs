using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRComposer.Voice
{
    /// <summary>
    /// ElevenLabs settings from a .env file that is never committed (it is in .gitignore and
    /// ignore.conf). Looked up in: the project root (next to Assets) in the editor, next to the
    /// executable in a PC build, and Application.persistentDataPath. Environment variables win.
    ///   ELEVENLABS_API_KEY=sk_...
    ///   ELEVENLABS_AGENT_ID=agent_...   (written by Tools > VR Composer > Create/Update ElevenLabs Agent)
    /// A key shipped inside a build can be extracted: fine for a demo, not for a release.
    /// </summary>
    public static class ElevenLabsConfig
    {
        public const string KeyVar = "ELEVENLABS_API_KEY";
        public const string AgentVar = "ELEVENLABS_AGENT_ID";

        public static string ApiKey { get { return Get(KeyVar); } }
        public static string AgentId { get { return Get(AgentVar); } }

        public static string EnvPath
        {
            get
            {
                foreach (var p in Candidates()) if (File.Exists(p)) return p;
                return Candidates()[0];
            }
        }

        private static List<string> Candidates()
        {
            var l = new List<string>();
            try { l.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".env"))); } catch { }
            try { l.Add(Path.Combine(Application.persistentDataPath, ".env")); } catch { }
            return l;
        }

        public static string Get(string name)
        {
            var env = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            string v;
            return Read().TryGetValue(name, out v) ? v : "";
        }

        public static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>();
            foreach (var p in Candidates())
            {
                if (!File.Exists(p)) continue;
                foreach (var raw in File.ReadAllLines(p))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim().Trim('"', '\'');
                    if (!d.ContainsKey(k)) d[k] = v;
                }
                break;
            }
            return d;
        }

        /// <summary>Sets one value in the .env file (keeps the other lines).</summary>
        public static void Set(string name, string value)
        {
            string path = EnvPath;
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].TrimStart().StartsWith(name + "=")) { lines[i] = name + "=" + value; found = true; }
            if (!found) lines.Add(name + "=" + value);
            File.WriteAllLines(path, lines);
        }
    }
}
