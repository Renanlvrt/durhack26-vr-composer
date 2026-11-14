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
        public static readonly VenueId[] Order = { VenueId.Studio, VenueId.GoldenHall, VenueId.RecitalRoom, VenueId.JazzClub, VenueId.Cathedral };

        public VenueId Current { get; private set; }
        public bool Busy { get; private set; }

        private VRComposerApp app;
        private VenueAudio venueAudio;
        private VenueInfo venue;
        private Transform stage;
        private GameObject fader;
        private Material faderMat;
        private readonly List<Behaviour> disabledSceneLights = new List<Behaviour>();
        private readonly List<GameObject> hiddenSceneRoots = new List<GameObject>();
        private UnityEngine.Rendering.AmbientMode origAmbientMode;
        private Color origAmbient;
        private int lastBeat = -1;

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
        }

        private void OnDestroy()
        {
            if (app != null && app.Player != null)
            {
                app.Player.NoteStarted -= OnNoteStarted;
                app.Player.Finished -= OnFinished;
            }
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
                RestoreScene();
                Current = id;

                if (id != VenueId.Studio)
                {
                    PlaceStage();
                    HideScene();
                    venue = Venues.Create(id, stage);
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = venue.ambient;
                }
                else
                {
                    RenderSettings.ambientMode = origAmbientMode;
                    RenderSettings.ambientLight = origAmbient;
                }
            }
            catch (System.Exception e)
            {
                // Never leave the user in the dark: fall back to the studio.
                Debug.LogError("[VRComposer] Could not build venue " + id + ": " + e);
                if (venue != null && venue.root != null) Destroy(venue.root);
                venue = null;
                RestoreScene();
                Current = VenueId.Studio;
                RenderSettings.ambientMode = origAmbientMode;
                RenderSettings.ambientLight = origAmbient;
            }
            if (venueAudio != null && venueAudio.enabled) { try { venueAudio.SetVenue(Current); } catch (System.Exception e) { Debug.LogWarning(e); } }
            try { app.OnThemeChanged(Current, venue != null ? venue.instrument : -1); } catch (System.Exception e) { Debug.LogException(e); }

            yield return null;
            yield return Fade(0f, 0.45f);
            Busy = false;
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
            foreach (var name in new[] { "Environment", "Teleport Area Setup" })
            {
                var go = GameObject.Find(name);
                if (go != null && go.activeSelf) { go.SetActive(false); hiddenSceneRoots.Add(go); }
            }
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            {
                if (l.transform.IsChildOf(stage) || !l.enabled) continue;
                l.enabled = false;
                disabledSceneLights.Add(l);
            }
        }

        private void RestoreScene()
        {
            foreach (var go in hiddenSceneRoots) if (go != null) go.SetActive(true);
            hiddenSceneRoots.Clear();
            foreach (var l in disabledSceneLights) if (l != null) l.enabled = true;
            disabledSceneLights.Clear();
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

        private void OnNoteStarted(int staff, int key, float seconds)
        {
            if (venue == null) return;
            foreach (var m in venue.players) if (m != null && m.playing) m.OnNote(key, seconds);
        }

        private void OnFinished()
        {
            if (venue == null || venueAudio == null) return;
            float len = 4f;
            try { len = venueAudio.PlayApplause(); } catch (System.Exception e) { Debug.LogWarning(e); }
            foreach (var a in venue.audience) if (a != null) a.Applaud(len * 0.8f);
            foreach (var m in venue.players) if (m != null) m.TakeBow();
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
                foreach (var m in venue.audience) if (m != null) m.OnBeat(beat);
            }
        }
    }
}
