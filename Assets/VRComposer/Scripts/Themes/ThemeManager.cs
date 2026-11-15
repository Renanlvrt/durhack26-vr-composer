using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace VRComposer
{
    /// <summary>
    /// Venue themes (durhack.4): the room around you while you compose. Musicians play the notes
    /// as they sound, the room's reverb colours the music, and after a full (non-looping)
    /// play-through the audience applauds and the musicians bow.
    /// </summary>
    public class ThemeManager : MonoBehaviour
    {
        // Studio (the empty template room) is no longer offered: you always compose in a venue.
        // VenueId.Studio stays internally as the "no venue yet / fallback" state.
        public static readonly VenueId[] Order = { VenueId.GoldenHall, VenueId.RecitalRoom, VenueId.JazzClub, VenueId.Cathedral };

        public VenueId Current { get; private set; }
        public bool Busy { get; private set; }

        private VRComposerApp app;
        private VenueAudio venueAudio;
        private VenueInfo venue;
        private Transform stage;
        private GameObject fader;
        private Material faderMat;
        private readonly List<Behaviour> disabledSceneLights = new List<Behaviour>();
        private readonly List<Renderer> hiddenSceneRenderers = new List<Renderer>();
        private GameObject safetyFloor;
        private UnityEngine.Rendering.AmbientMode origAmbientMode;
        private Color origAmbient;
        private bool savedCamera;
        private CameraClearFlags origClear;
        private Color origBackground;
        private Material origSkybox;
        private bool origFog;
        private int lastBeat = -1;

        // your band: one musician per staff of the score, rebuilt when instruments change
        private readonly List<Musician> band = new List<Musician>();
        private readonly List<int> bandStaff = new List<int>();
        private string bandSignature = "";

        public static string Title(VenueId v)
        {
            return v == VenueId.Studio ? "Studio" : Venues.Describe(v).title;
        }

        public static string Subtitle(VenueId v)
        {
            return v == VenueId.Studio ? "Your own space · neutral sound" : Venues.Describe(v).subtitle;
        }

        public void Init(VRComposerApp owner)
        {
            app = owner;
            origAmbientMode = RenderSettings.ambientMode;
            origAmbient = RenderSettings.ambientLight;

            var listener = Camera.main != null ? Camera.main.GetComponent<AudioListener>() : null;
            if (listener == null) listener = FindAnyObjectByType<AudioListener>();
            venueAudio = gameObject.AddComponent<VenueAudio>();
            if (listener != null) venueAudio.Init(listener);
            else Debug.LogWarning("[VRComposer] No AudioListener: venue reverb disabled.");

            stage = new GameObject("VR Composer Venue Stage").transform;
            app.Player.NoteStarted += OnNoteStarted;
            app.Player.Finished += OnFinished;
            app.Score.Changed += OnScoreChanged;
        }

        private void OnDestroy()
        {
            if (app != null && app.Player != null)
            {
                app.Player.NoteStarted -= OnNoteStarted;
                app.Player.Finished -= OnFinished;
            }
            if (app != null && app.Score != null) app.Score.Changed -= OnScoreChanged;
        }

        public void Next() { Set(Order[(System.Array.IndexOf(Order, Current) + 1) % Order.Length]); }

        public void Set(VenueId id)
        {
            if (Busy) return;
            StartCoroutine(Switch(id));
        }

        private IEnumerator Switch(VenueId id)
        {
            Busy = true;
            yield return Fade(1f, 0.35f);

            try
            {
                if (venue != null) { Destroy(venue.root); venue = null; }
                band.Clear(); bandStaff.Clear(); bandSignature = "";
                RestoreScene();
                Current = id;

                if (id != VenueId.Studio)
                {
                    PlaceStage();
                    HideScene();
                    venue = Venues.Create(id, stage);
                    RebuildBand();
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = venue.ambient;
                    SetIndoorBackground(venue.ambient * 0.3f);
                    Debug.Log("[VRComposer] Venue " + id + " built (" + venue.root.GetComponentsInChildren<Renderer>().Length + " meshes).");
                }
                else
                {
                    RenderSettings.ambientMode = origAmbientMode;
                    RenderSettings.ambientLight = origAmbient;
                    RestoreBackground();
                }
            }
            catch (System.Exception e)
            {
                // Never leave the user in the dark: fall back to the studio.
                Debug.LogError("[VRComposer] Could not build venue " + id + ": " + e);
                if (app.View != null) app.View.SetIdleReadout("Could not load " + Title(id) + ": " + e.GetType().Name + " " + e.Message);
                if (venue != null && venue.root != null) Destroy(venue.root);
                venue = null;
                RestoreScene();
                Current = VenueId.Studio;
                RenderSettings.ambientMode = origAmbientMode;
                RenderSettings.ambientLight = origAmbient;
                RestoreBackground();
            }
            if (venueAudio != null && venueAudio.enabled) { try { venueAudio.SetVenue(Current); } catch (System.Exception e) { Debug.LogWarning(e); } }
            try { app.OnThemeChanged(Current, venue != null ? venue.instrument : -1); } catch (System.Exception e) { Debug.LogException(e); }

            yield return null;
            yield return Fade(0f, 0.45f);
            Busy = false;
        }

        /// <summary>
        /// Indoors there is no sky: the camera clears to a dark colour (as in durhack.4) and the
        /// skybox is removed, so any gap in the venue geometry looks like a dark room, not open sky.
        /// </summary>
        private void SetIndoorBackground(Color c)
        {
            var cam = Camera.main;
            if (cam != null && !savedCamera)
            {
                savedCamera = true;
                origClear = cam.clearFlags;
                origBackground = cam.backgroundColor;
                origSkybox = RenderSettings.skybox;
                origFog = RenderSettings.fog;
            }
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                c.a = 1f;
                cam.backgroundColor = c;
            }
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            DynamicGI.UpdateEnvironment();
        }

        private void RestoreBackground()
        {
            if (!savedCamera) return;
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = origClear;
                cam.backgroundColor = origBackground;
            }
            RenderSettings.skybox = origSkybox;
            RenderSettings.fog = origFog;
            DynamicGI.UpdateEnvironment();
            savedCamera = false;
        }

        /// <summary>Re-aligns the venue with where the user now stands and looks (Recenter).</summary>
        public void Realign()
        {
            if (venue == null) return;
            PlaceStage();
        }

        /// <summary>Venues are built around a user standing at the origin facing +Z.</summary>
        private void PlaceStage()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var head = cam.transform;
            float floorY = head.position.y - 1.6f;
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null) floorY = origin.transform.position.y;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            stage.SetPositionAndRotation(new Vector3(head.position.x, floorY, head.position.z), Quaternion.LookRotation(fwd.normalized));
        }

        private void HideScene()
        {
            // The template's room and its sun would clash with the venue's own floor and lights.
            // Only its visuals are hidden: its colliders stay, because they are the floor the XR rig
            // stands on (gravity) and the teleport areas. Deactivating them made you fall through.
            foreach (var name in new[] { "Environment", "Teleport Area Setup" })
            {
                var go = GameObject.Find(name);
                if (go == null || !go.activeInHierarchy) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    hiddenSceneRenderers.Add(r);
                }
            }
            EnsureSafetyFloor();
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            {
                if (l.transform.IsChildOf(stage) || !l.enabled) continue;
                l.enabled = false;
                disabledSceneLights.Add(l);
            }
        }

        private void RestoreScene()
        {
            foreach (var r in hiddenSceneRenderers) if (r != null) r.enabled = true;
            hiddenSceneRenderers.Clear();
            if (safetyFloor != null) safetyFloor.SetActive(false);
            foreach (var l in disabledSceneLights) if (l != null) l.enabled = true;
            disabledSceneLights.Clear();
        }

        /// <summary>
        /// Venue floors are visual only (no colliders, to keep Quest physics cheap), so an invisible
        /// floor under the stage catches the rig in case the scene has no floor collider of its own.
        /// </summary>
        private void EnsureSafetyFloor()
        {
            if (safetyFloor == null)
            {
                safetyFloor = new GameObject("VR Composer Venue Floor (collider)");
                var box = safetyFloor.AddComponent<BoxCollider>();
                box.size = new Vector3(60f, 0.2f, 60f);
                box.center = new Vector3(0f, -0.1f, 0f);   // top surface exactly at floor height
            }
            safetyFloor.transform.SetParent(stage, false);
            safetyFloor.transform.localPosition = Vector3.zero;
            safetyFloor.transform.localRotation = Quaternion.identity;
            safetyFloor.SetActive(true);
        }

        private IEnumerator Fade(float to, float seconds)
        {
            var cam = Camera.main;
            if (cam == null) yield break;
            if (fader == null)
            {
                fader = GameObject.CreatePrimitive(PrimitiveType.Quad);
                fader.name = "VR Composer Fade";
                Destroy(fader.GetComponent<Collider>());
                var sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("UI/Default");
                if (sh == null) { Destroy(fader); fader = null; yield break; } // no fade rather than a crash
                faderMat = new Material(sh);
                faderMat.renderQueue = 5000;
                fader.GetComponent<MeshRenderer>().sharedMaterial = faderMat;
            }
            fader.transform.SetParent(cam.transform, false);
            fader.transform.localPosition = new Vector3(0, 0, cam.nearClipPlane + 0.02f);
            fader.transform.localScale = Vector3.one * 2f;
            fader.SetActive(true);
            float from = faderMat.color.a;
            for (float t = 0; t < seconds; t += Time.unscaledDeltaTime)
            {
                faderMat.color = new Color(0, 0, 0, Mathf.Lerp(from, to, t / seconds));
                yield return null;
            }
            faderMat.color = new Color(0, 0, 0, to);
            if (to <= 0f) fader.SetActive(false);
        }

        private void OnScoreChanged()
        {
            if (venue != null && Signature() != bandSignature) RebuildBand();
        }

        private string Signature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var st in app.Score.staves) sb.Append(st.program).Append(',');
            return sb.ToString();
        }

        /// <summary>
        /// One musician per instrument in your score, on the venue's band row: add a piano and a
        /// pianist walks on. Big instruments (piano, harp, marimba) take the ends of the row.
        /// </summary>
        private void RebuildBand()
        {
            foreach (var m in band) if (m != null) Destroy(m.gameObject);
            band.Clear();
            bandStaff.Clear();
            bandSignature = Signature();
            if (venue == null || venue.dynamicRoot == null) return;

            var staves = app.Score.staves;
            int n = staves.Count;
            // seat order: big instruments at the ends, the rest in score order (Violin I on the left)
            var order = new List<int>();
            var big = new List<int>();
            for (int i = 0; i < n; i++) (Roles.IsBig(Roles.ForProgram(staves[i].program)) ? big : order).Add(i);
            for (int k = 0; k < big.Count; k++) { if (k % 2 == 0) order.Insert(0, big[k]); else order.Add(big[k]); }

            for (int slot = 0; slot < order.Count; slot++)
            {
                int si = order[slot];
                var staff = staves[si];
                var role = Roles.ForProgram(staff.program);
                Vector3 pos, look;
                venue.BandSlot(slot, n, out pos, out look);
                // keyboards, harp and marimba turn side-on so the instrument doesn't hide the others
                if (Roles.IsBig(role))
                {
                    float side = pos.x <= 0f ? 1f : -1f;
                    look = pos + new Vector3(side, 0f, -0.35f);
                    if (role == Role.Piano) pos += new Vector3(-side * 0.5f, 0f, 0.3f);
                    // big instruments sit further back, away from you, so they don't poke through the score panels
                    var away = new Vector3(pos.x, 0f, pos.z);
                    if (away.sqrMagnitude > 0.01f) pos += away.normalized * (role == Role.Piano ? 1.1f : 0.7f);
                    look = pos + new Vector3(side, 0f, -0.35f);
                }
                var outfit = venue.jazzOutfits ? Outfit.Jazz(si + 4) : Outfit.ConcertBlack(si * 3 + 2);
                var m = Musician.Create(venue.dynamicRoot, staff.name, role, outfit, pos, look, !Roles.Stands(role), true);
                band.Add(m);
                bandStaff.Add(si);
            }
        }

        private void OnNoteStarted(int staff, int key, float seconds)
        {
            if (venue == null) return;
            // each of your musicians plays their own staff; the venue's ensemble plays along with everything
            for (int i = 0; i < band.Count; i++) if (band[i] != null && bandStaff[i] == staff) band[i].OnNote(key, seconds);
            foreach (var m in venue.players) if (m != null && m.playing) m.OnNote(key, seconds);
        }

        private void OnFinished()
        {
            if (venue == null || venueAudio == null) return;
            float len = 4f;
            try { len = venueAudio.PlayApplause(); } catch (System.Exception e) { Debug.LogWarning(e); }
            foreach (var a in venue.audience) if (a != null) a.Applaud(len * 0.8f);
            foreach (var m in venue.players) if (m != null) m.TakeBow();
            foreach (var m in band) if (m != null) m.TakeBow();
        }

        private void Update()
        {
            if (app == null || app.Player == null) return;
            Conductor.IsPlaying = app.Player.IsPlaying;
            Conductor.BeatsPerSecond = app.Score.tempoBpm / 60f;
            if (venue == null || !app.Player.IsPlaying) { lastBeat = -1; return; }
            int beat = Mathf.FloorToInt(app.Player.PositionBeats);
            if (beat != lastBeat)
            {
                lastBeat = beat;
                foreach (var m in venue.players) if (m != null) m.OnBeat(beat);
                foreach (var m in band) if (m != null) m.OnBeat(beat);
                foreach (var m in venue.audience) if (m != null) m.OnBeat(beat);
            }
        }
    }
}
