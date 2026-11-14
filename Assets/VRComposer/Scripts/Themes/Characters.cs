using System.Collections.Generic;
using UnityEngine;

namespace VRComposer
{
    public enum Role { Violin, Cello, DoubleBass, Piano, Saxophone, Trumpet, Horn, Trombone, Tuba, Drums, Audience }

    public struct Outfit
    {
        public Color jacket, trousers, shirt, tie, skin, hair;
        public bool dress, longHair, bowTie;
        static readonly Color[] Skins = { new Color(0.96f, 0.8f, 0.67f), new Color(0.86f, 0.66f, 0.5f), new Color(0.7f, 0.5f, 0.34f), new Color(0.5f, 0.33f, 0.22f), new Color(0.36f, 0.24f, 0.16f) };
        static readonly Color[] Hairs = { new Color(0.08f, 0.06f, 0.05f), new Color(0.3f, 0.18f, 0.1f), new Color(0.7f, 0.55f, 0.32f), new Color(0.55f, 0.55f, 0.55f), new Color(0.18f, 0.1f, 0.07f) };

        /// Concert black: tails and white shirt, or a long black dress.
        public static Outfit ConcertBlack(int seed)
        {
            bool dress = seed % 3 == 1;
            return new Outfit
            {
                jacket = new Color(0.07f, 0.07f, 0.09f), trousers = new Color(0.06f, 0.06f, 0.07f), shirt = dress ? Skins[seed % 5] : new Color(0.95f, 0.95f, 0.95f),
                tie = new Color(0.05f, 0.05f, 0.05f), skin = Skins[(seed * 7 + 3) % 5], hair = Hairs[(seed * 3 + 1) % 5], dress = dress, longHair = dress || seed % 5 == 2, bowTie = !dress
            };
        }

        public static Outfit Jazz(int seed)
        {
            var jackets = new[] { new Color(0.15f, 0.15f, 0.2f), new Color(0.3f, 0.22f, 0.18f), new Color(0.85f, 0.83f, 0.78f), new Color(0.1f, 0.1f, 0.1f) };
            return new Outfit
            {
                jacket = jackets[seed % jackets.Length], trousers = new Color(0.1f, 0.1f, 0.12f), shirt = seed % 2 == 0 ? new Color(0.9f, 0.9f, 0.95f) : new Color(0.45f, 0.12f, 0.15f),
                tie = new Color(0.5f, 0.1f, 0.1f), skin = Skins[(seed * 5 + 2) % 5], hair = Hairs[(seed + 2) % 5], longHair = seed % 4 == 3
            };
        }

        public static Outfit Casual(int seed)
        {
            var tops = new[] { new Color(0.55f, 0.15f, 0.2f), new Color(0.2f, 0.3f, 0.5f), new Color(0.3f, 0.45f, 0.3f), new Color(0.7f, 0.65f, 0.55f), new Color(0.25f, 0.25f, 0.28f), new Color(0.6f, 0.45f, 0.2f) };
            return new Outfit
            {
                jacket = tops[seed % tops.Length], trousers = new Color(0.15f, 0.15f, 0.2f), shirt = tops[(seed + 2) % tops.Length], tie = tops[(seed + 1) % tops.Length],
                skin = Skins[(seed * 3) % 5], hair = Hairs[(seed * 2 + 1) % 5], dress = seed % 4 == 1, longHair = seed % 3 == 1
            };
        }
    }

    /// A procedurally built, procedurally animated person. Arms use two-bone IK so hands
    /// really hold the instrument; bowing, fingering and breathing follow the music.
    public class Musician : MonoBehaviour
    {
        public Role role;
        public bool seated = true;
        public bool playing;            // part of the section that plays the melody
        Outfit o;
        Transform hips, spine, chest, head;
        Transform upperL, foreL, handL, upperR, foreR, handR, bow;
        float phase, bowPos = 0.5f, bowDir = 1f, bowSpeed = 1f, pulse, clapUntil, bowUntil, bowStart;
        int pitch = 67;
        Vector3 shoulderL, shoulderR;
        const float UpperLen = 0.29f, ForeLen = 0.27f;

        static readonly Color Wood = new Color(0.45f, 0.22f, 0.08f), Gold = new Color(0.86f, 0.66f, 0.25f), Black = new Color(0.04f, 0.04f, 0.05f), Silver = new Color(0.8f, 0.8f, 0.82f);

        public static Musician Create(Transform parent, string name, Role role, Outfit outfit, Vector3 pos, Vector3 lookAt, bool seated, bool playing)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var dir = lookAt - pos; dir.y = 0;
            if (dir.sqrMagnitude > 0.001f) go.transform.localRotation = Quaternion.LookRotation(dir.normalized);
            var m = go.AddComponent<Musician>();
            m.role = role; m.o = outfit; m.seated = seated; m.playing = playing;
            m.phase = Random.value * 10f;
            m.BuildBody();
            return m;
        }

        GameObject P(PrimitiveType t, Transform parent, string n, Vector3 pos, Vector3 scale, Color c, Vector3 euler = default) => Build.Prim(t, parent, n, pos, scale, c, euler);

        void BuildBody()
        {
            float hipY = seated ? 0.47f : 0.95f;
            hips = Build.Child(transform, "Hips", new Vector3(0, hipY, 0)).transform;
            // legs
            if (seated)
            {
                if (role != Role.Audience) { P(PrimitiveType.Cube, transform, "Chair seat", new Vector3(0, 0.44f, -0.02f), new Vector3(0.44f, 0.04f, 0.42f), new Color(0.25f, 0.14f, 0.08f)); P(PrimitiveType.Cube, transform, "Chair back", new Vector3(0, 0.7f, -0.22f), new Vector3(0.42f, 0.5f, 0.04f), new Color(0.25f, 0.14f, 0.08f)); }
                if (o.dress) P(PrimitiveType.Cylinder, hips, "Skirt", new Vector3(0, -0.2f, 0.16f), new Vector3(0.42f, 0.25f, 0.5f), o.trousers);
                for (int s = -1; s <= 1; s += 2)
                {
                    if (!o.dress) P(PrimitiveType.Capsule, hips, "Thigh", new Vector3(0.1f * s, 0, 0.2f), new Vector3(0.14f, 0.24f, 0.14f), o.trousers, new Vector3(90, 0, 0));
                    P(PrimitiveType.Capsule, hips, "Shin", new Vector3(0.1f * s, -0.24f, 0.42f), new Vector3(0.11f, 0.24f, 0.11f), o.dress ? o.skin : o.trousers);
                    P(PrimitiveType.Cube, hips, "Shoe", new Vector3(0.1f * s, -0.45f, 0.47f), new Vector3(0.1f, 0.06f, 0.24f), Black);
                }
            }
            else
            {
                if (o.dress) P(PrimitiveType.Cylinder, hips, "Skirt", new Vector3(0, -0.45f, 0), new Vector3(0.4f, 0.45f, 0.32f), o.trousers);
                for (int s = -1; s <= 1; s += 2)
                {
                    if (!o.dress) P(PrimitiveType.Capsule, hips, "Leg", new Vector3(0.1f * s, -0.46f, 0), new Vector3(0.14f, 0.47f, 0.14f), o.trousers);
                    P(PrimitiveType.Cube, hips, "Shoe", new Vector3(0.1f * s, -0.92f, 0.05f), new Vector3(0.1f, 0.06f, 0.26f), Black);
                }
            }
            spine = Build.Child(hips, "Spine", Vector3.zero).transform;
            P(PrimitiveType.Capsule, spine, "Torso", new Vector3(0, 0.28f, 0), new Vector3(0.38f, 0.29f, 0.24f), o.jacket);
            if (!o.dress && role != Role.Audience)
            {
                P(PrimitiveType.Cube, spine, "Shirt front", new Vector3(0, 0.42f, 0.105f), new Vector3(0.1f, 0.2f, 0.02f), o.shirt);
                if (o.bowTie) P(PrimitiveType.Cube, spine, "Bow tie", new Vector3(0, 0.52f, 0.115f), new Vector3(0.08f, 0.03f, 0.02f), o.tie);
                else P(PrimitiveType.Cube, spine, "Tie", new Vector3(0, 0.42f, 0.118f), new Vector3(0.035f, 0.18f, 0.01f), o.tie);
            }
            chest = Build.Child(spine, "Chest", new Vector3(0, 0.5f, 0)).transform;
            P(PrimitiveType.Capsule, chest, "Neck", new Vector3(0, 0.07f, 0), new Vector3(0.09f, 0.06f, 0.09f), o.skin);
            head = Build.Child(chest, "Head", new Vector3(0, 0.2f, 0)).transform;
            P(PrimitiveType.Sphere, head, "Face", Vector3.zero, new Vector3(0.18f, 0.23f, 0.2f), o.skin);
            P(PrimitiveType.Sphere, head, "Nose", new Vector3(0, -0.01f, 0.1f), new Vector3(0.035f, 0.05f, 0.04f), o.skin * 0.95f);
            P(PrimitiveType.Sphere, head, "Ear", new Vector3(-0.09f, 0, 0), new Vector3(0.03f, 0.06f, 0.04f), o.skin);
            P(PrimitiveType.Sphere, head, "Ear", new Vector3(0.09f, 0, 0), new Vector3(0.03f, 0.06f, 0.04f), o.skin);
            P(PrimitiveType.Sphere, head, "Hair", new Vector3(0, 0.045f, -0.02f), new Vector3(0.195f, 0.17f, 0.21f), o.hair);
            if (o.longHair) P(PrimitiveType.Capsule, head, "Long hair", new Vector3(0, -0.06f, -0.07f), new Vector3(0.19f, 0.14f, 0.1f), o.hair);

            shoulderL = new Vector3(-0.2f, -0.02f, 0); shoulderR = new Vector3(0.2f, -0.02f, 0);
            upperL = P(PrimitiveType.Capsule, chest, "Upper arm L", Vector3.zero, new Vector3(0.1f, UpperLen / 2, 0.1f), o.jacket).transform;
            foreL = P(PrimitiveType.Capsule, chest, "Forearm L", Vector3.zero, new Vector3(0.085f, ForeLen / 2, 0.085f), o.jacket).transform;
            handL = P(PrimitiveType.Sphere, chest, "Hand L", Vector3.zero, new Vector3(0.07f, 0.09f, 0.05f), role == Role.Audience ? o.skin : o.skin).transform;
            upperR = P(PrimitiveType.Capsule, chest, "Upper arm R", Vector3.zero, new Vector3(0.1f, UpperLen / 2, 0.1f), o.jacket).transform;
            foreR = P(PrimitiveType.Capsule, chest, "Forearm R", Vector3.zero, new Vector3(0.085f, ForeLen / 2, 0.085f), o.jacket).transform;
            handR = P(PrimitiveType.Sphere, chest, "Hand R", Vector3.zero, new Vector3(0.07f, 0.09f, 0.05f), o.skin).transform;
            BuildInstrument();
            UpdatePose(0f);
        }

        void BuildInstrument()
        {
            switch (role)
            {
                case Role.Violin:
                    // held under the chin on the left shoulder, scroll pointing forward-left
                    var v = Build.Child(chest, "Violin", new Vector3(-0.12f, 0.06f, 0.16f)).transform;
                    v.localRotation = Quaternion.Euler(10, -38, -25);
                    P(PrimitiveType.Sphere, v, "Body", new Vector3(0, 0, 0.05f), new Vector3(0.2f, 0.045f, 0.34f), Wood);
                    P(PrimitiveType.Cube, v, "Neck", new Vector3(0, 0.02f, 0.3f), new Vector3(0.03f, 0.02f, 0.22f), Black);
                    bow = P(PrimitiveType.Cylinder, chest, "Bow", Vector3.zero, new Vector3(0.008f, 0.34f, 0.008f), new Color(0.9f, 0.85f, 0.7f)).transform;
                    break;
                case Role.Cello:
                    var c = Build.Child(hips, "Cello", new Vector3(0, 0.12f, 0.36f)).transform;
                    c.localRotation = Quaternion.Euler(-12, 0, 0);
                    P(PrimitiveType.Sphere, c, "Body", Vector3.zero, new Vector3(0.42f, 0.72f, 0.2f), Wood);
                    P(PrimitiveType.Cube, c, "Neck", new Vector3(0, 0.6f, -0.02f), new Vector3(0.05f, 0.55f, 0.04f), Black);
                    P(PrimitiveType.Cylinder, c, "Spike", new Vector3(0, -0.45f, 0), new Vector3(0.01f, 0.12f, 0.01f), Silver);
                    bow = P(PrimitiveType.Cylinder, chest, "Bow", Vector3.zero, new Vector3(0.009f, 0.36f, 0.009f), new Color(0.9f, 0.85f, 0.7f)).transform;
                    break;
                case Role.DoubleBass:
                    var b = Build.Child(transform, "Double bass", new Vector3(-0.12f, 0.75f, 0.3f)).transform;
                    b.localRotation = Quaternion.Euler(-8, 0, 12);
                    P(PrimitiveType.Sphere, b, "Body", Vector3.zero, new Vector3(0.6f, 1.15f, 0.3f), Wood);
                    P(PrimitiveType.Cube, b, "Neck", new Vector3(0, 0.88f, -0.02f), new Vector3(0.06f, 0.75f, 0.05f), Black);
                    break;
                case Role.Piano:
                    var pn = Build.Child(transform, "Grand piano", new Vector3(0, 0, 0.62f)).transform;
                    P(PrimitiveType.Cube, pn, "Case", new Vector3(0, 0.82f, 0.55f), new Vector3(1.5f, 0.3f, 1.4f), Black);
                    P(PrimitiveType.Cube, pn, "Lid", new Vector3(0.3f, 1.25f, 0.7f), new Vector3(1.3f, 0.02f, 1.2f), Black, new Vector3(0, 0, 35));
                    P(PrimitiveType.Cube, pn, "Keys", new Vector3(0, 0.74f, -0.12f), new Vector3(1.25f, 0.03f, 0.16f), new Color(0.95f, 0.95f, 0.92f));
                    for (int k = 0; k < 3; k++) P(PrimitiveType.Cylinder, pn, "Leg", new Vector3(k == 0 ? -0.65f : k == 1 ? 0.65f : 0, 0.33f, k == 2 ? 1.1f : 0.1f), new Vector3(0.08f, 0.33f, 0.08f), Black);
                    break;
                case Role.Saxophone:
                    var sx = Build.Child(chest, "Saxophone", new Vector3(0.03f, -0.05f, 0.17f)).transform;
                    sx.localRotation = Quaternion.Euler(0, 0, -12);
                    P(PrimitiveType.Cylinder, sx, "Body", new Vector3(0, -0.12f, 0), new Vector3(0.05f, 0.2f, 0.05f), Gold);
                    P(PrimitiveType.Cylinder, sx, "Neck", new Vector3(-0.03f, 0.15f, -0.03f), new Vector3(0.02f, 0.08f, 0.02f), Gold, new Vector3(30, 0, 30));
                    P(PrimitiveType.Sphere, sx, "Bell", new Vector3(0.05f, -0.32f, 0.04f), new Vector3(0.11f, 0.08f, 0.11f), Gold);
                    break;
                case Role.Trumpet:
                    var tr = Build.Child(chest, "Trumpet", new Vector3(0, 0.17f, 0.12f)).transform;
                    P(PrimitiveType.Cylinder, tr, "Lead pipe", new Vector3(0, 0, 0.16f), new Vector3(0.02f, 0.17f, 0.02f), Gold, new Vector3(90, 0, 0));
                    P(PrimitiveType.Cube, tr, "Valves", new Vector3(0, 0.02f, 0.14f), new Vector3(0.05f, 0.07f, 0.06f), Gold);
                    P(PrimitiveType.Cylinder, tr, "Bell", new Vector3(0, 0, 0.36f), new Vector3(0.1f, 0.03f, 0.1f), Gold, new Vector3(90, 0, 0));
                    break;
                case Role.Trombone:
                    var tb = Build.Child(chest, "Trombone", new Vector3(0, 0.17f, 0.12f)).transform;
                    P(PrimitiveType.Cylinder, tb, "Slide", new Vector3(0, -0.03f, 0.35f), new Vector3(0.02f, 0.35f, 0.02f), Gold, new Vector3(90, 0, 0));
                    P(PrimitiveType.Cylinder, tb, "Bell pipe", new Vector3(-0.06f, 0.02f, 0.15f), new Vector3(0.025f, 0.2f, 0.025f), Gold, new Vector3(90, 0, 0));
                    P(PrimitiveType.Cylinder, tb, "Bell", new Vector3(-0.06f, 0.02f, 0.36f), new Vector3(0.14f, 0.03f, 0.14f), Gold, new Vector3(90, 0, 0));
                    break;
                case Role.Horn:
                    var hn = Build.Child(chest, "Horn", new Vector3(0.12f, -0.2f, 0.16f)).transform;
                    P(PrimitiveType.Cylinder, hn, "Coils", Vector3.zero, new Vector3(0.24f, 0.03f, 0.24f), Gold, new Vector3(0, 0, 70));
                    P(PrimitiveType.Cylinder, hn, "Bell", new Vector3(0.14f, -0.05f, -0.05f), new Vector3(0.16f, 0.04f, 0.16f), Gold, new Vector3(0, 0, 70));
                    break;
                case Role.Tuba:
                    var tu = Build.Child(chest, "Tuba", new Vector3(-0.05f, -0.15f, 0.2f)).transform;
                    P(PrimitiveType.Sphere, tu, "Body", Vector3.zero, new Vector3(0.3f, 0.55f, 0.22f), Gold);
                    P(PrimitiveType.Cylinder, tu, "Bell", new Vector3(0.02f, 0.32f, 0), new Vector3(0.32f, 0.04f, 0.32f), Gold);
                    break;
                case Role.Drums:
                    var kit = Build.Child(transform, "Drum kit", new Vector3(0, 0, 0.45f)).transform;
                    P(PrimitiveType.Cylinder, kit, "Bass drum", new Vector3(0, 0.28f, 0.15f), new Vector3(0.5f, 0.17f, 0.5f), new Color(0.6f, 0.1f, 0.12f), new Vector3(90, 0, 0));
                    P(PrimitiveType.Cylinder, kit, "Snare", new Vector3(-0.25f, 0.62f, -0.05f), new Vector3(0.34f, 0.07f, 0.34f), new Color(0.9f, 0.9f, 0.9f));
                    P(PrimitiveType.Cylinder, kit, "Ride", new Vector3(0.38f, 0.95f, 0.1f), new Vector3(0.5f, 0.005f, 0.5f), Gold);
                    P(PrimitiveType.Cylinder, kit, "Hi-hat", new Vector3(-0.5f, 0.85f, 0.05f), new Vector3(0.34f, 0.005f, 0.34f), Gold);
                    break;
            }
        }

        // ---------- music hooks ----------
        public void OnNote(int midi, float seconds)
        {
            pitch = midi;
            pulse = 1f;
            if (role == Role.Violin || role == Role.Cello || role == Role.DoubleBass)
            {
                bowDir = -bowDir;                                // change bow direction on every note
                bowSpeed = 0.9f / Mathf.Max(0.12f, seconds);     // use most of the bow over the note
            }
        }

        public void OnBeat(int beat) { if (!playing) pulse = Mathf.Max(pulse, 0.5f); }   // listeners nod along

        public void Applaud(float seconds) { clapUntil = Time.time + seconds; }
        public void TakeBow() { bowStart = Time.time; bowUntil = Time.time + 2.2f; }

        // ---------- animation ----------
        void Update() => UpdatePose(Time.deltaTime);

        Vector3 SolveIK(Vector3 shoulder, Vector3 target, Vector3 pole, Transform upper, Transform fore, Transform hand)
        {
            var d = target - shoulder;
            float len = Mathf.Clamp(d.magnitude, 0.05f, UpperLen + ForeLen - 0.01f);
            var dir = d.normalized;
            // law of cosines: angle at the shoulder between the reach line and the upper arm
            float cosA = (UpperLen * UpperLen + len * len - ForeLen * ForeLen) / (2 * UpperLen * len);
            float a = Mathf.Acos(Mathf.Clamp(cosA, -1f, 1f));
            var bendAxis = Vector3.Cross(dir, pole).normalized;
            if (bendAxis.sqrMagnitude < 0.001f) bendAxis = Vector3.right;
            var elbow = shoulder + Quaternion.AngleAxis(-a * Mathf.Rad2Deg, bendAxis) * dir * UpperLen;
            var tgt = shoulder + dir * len;
            Segment(upper, shoulder, elbow);
            Segment(fore, elbow, tgt);
            hand.localPosition = tgt;
            hand.localRotation = Quaternion.LookRotation((tgt - elbow).normalized, Vector3.up);
            return tgt;
        }

        static void Segment(Transform t, Vector3 a, Vector3 b)
        {
            t.localPosition = (a + b) * 0.5f;
            t.localRotation = Quaternion.FromToRotation(Vector3.up, (b - a).normalized);
        }

        void UpdatePose(float dt)
        {
            float t = Time.time + phase;
            bool sounding = playing && Conductor.IsPlaying;
            pulse = Mathf.MoveTowards(pulse, 0f, dt * 3f);

            // breathing and posture
            float breath = Mathf.Sin(t * 2f * Mathf.PI / 4.2f);
            spine.localScale = new Vector3(1f + breath * 0.008f, 1f + breath * 0.012f, 1f + breath * 0.01f);
            float sway = sounding ? Mathf.Sin(t * 1.3f) * 3f + pulse * 2.5f : Mathf.Sin(t * 0.4f) * 1f;
            float bowLean = 0f;
            if (Time.time < bowUntil)
            {
                float k = Mathf.Clamp01((Time.time - bowStart) / 2.2f);
                bowLean = Mathf.Sin(k * Mathf.PI) * 32f;     // performers take a bow at the end
            }
            spine.localRotation = Quaternion.Euler(bowLean + (role == Role.Saxophone && sounding ? pulse * 6f : 0f), 0, sway * (role == Role.Saxophone || role == Role.Violin ? 1.4f : 0.6f));

            // head: idle drift, nods on beats, players glance at the conductor
            float yaw = (Mathf.PerlinNoise(t * 0.15f, phase) - 0.5f) * 30f;
            float nod = pulse * (playing ? 6f : 4f) + Mathf.Sin(t * 0.7f) * 2f;
            if (role == Role.Violin) { yaw = -18f; nod += 14f; }     // chin rests on the violin
            head.localRotation = Quaternion.Euler(nod, yaw, role == Role.Violin ? -12f : 0f);

            // arms
            Vector3 lt, rt, poleL = new Vector3(-1, -0.6f, -0.3f), poleR = new Vector3(1, -0.6f, -0.3f);
            switch (role)
            {
                case Role.Violin:
                {
                    if (sounding) bowPos = Mathf.Clamp01(bowPos + bowDir * bowSpeed * dt); else bowPos = Mathf.MoveTowards(bowPos, 0.2f, dt);
                    lt = new Vector3(-0.32f, 0.08f, 0.38f) + new Vector3(0, (pitch - 67) * 0.002f, (pitch - 67) * 0.004f);  // left hand moves up the fingerboard with pitch
                    var contact = new Vector3(-0.12f, 0.04f, 0.2f);
                    rt = Vector3.Lerp(new Vector3(0.0f, 0.0f, 0.3f), new Vector3(0.34f, -0.14f, 0.18f), bowPos);
                    poleL = new Vector3(-0.3f, -1f, 0.2f);
                    var hr = SolveIK(shoulderR, rt, poleR, upperR, foreR, handR);
                    SolveIK(shoulderL, lt, poleL, upperL, foreL, handL);
                    PlaceBow(hr, contact, 0.68f);
                    return;
                }
                case Role.Cello:
                {
                    if (sounding) bowPos = Mathf.Clamp01(bowPos + bowDir * bowSpeed * dt); else bowPos = Mathf.MoveTowards(bowPos, 0.3f, dt);
                    lt = new Vector3(-0.08f, -0.05f - (pitch - 55) * 0.004f, 0.36f);
                    rt = Vector3.Lerp(new Vector3(0.02f, -0.42f, 0.42f), new Vector3(0.42f, -0.4f, 0.3f), bowPos);
                    var hr = SolveIK(shoulderR, rt, poleR, upperR, foreR, handR);
                    SolveIK(shoulderL, lt, poleL, upperL, foreL, handL);
                    PlaceBow(hr, new Vector3(-0.02f, -0.44f, 0.46f), 0.72f);
                    return;
                }
                case Role.DoubleBass:
                    lt = new Vector3(-0.2f, 0.1f, 0.32f);
                    rt = new Vector3(0.0f, -0.38f + pulse * 0.04f, 0.36f + pulse * 0.03f);   // pizzicato pluck
                    break;
                case Role.Piano:
                {
                    float x = Mathf.Clamp((pitch - 66) / 18f, -1f, 1f) * 0.32f;
                    lt = new Vector3(-0.22f + Mathf.Sin(t * 1.1f) * 0.02f, -0.3f + (sounding ? Mathf.Abs(Mathf.Sin(t * 5f)) * 0.012f : 0f), 0.42f);
                    rt = new Vector3(Mathf.Max(0.02f, x + 0.1f), -0.3f - pulse * 0.018f, 0.42f);
                    poleL = new Vector3(-1, -0.2f, -0.5f); poleR = new Vector3(1, -0.2f, -0.5f);
                    break;
                }
                case Role.Saxophone:
                    lt = new Vector3(0.0f, -0.08f, 0.2f); rt = new Vector3(0.06f, -0.27f + pulse * 0.01f, 0.22f);
                    break;
                case Role.Trumpet:
                case Role.Trombone:
                {
                    float lift = sounding ? 0f : -0.25f;                       // instruments come up to play
                    lt = new Vector3(-0.04f, 0.12f + lift, 0.28f);
                    rt = role == Role.Trombone ? new Vector3(0.02f, 0.08f + lift, 0.42f + (sounding ? Mathf.Sin(t * 2f) * 0.08f : 0f)) : new Vector3(0.04f, 0.14f + lift, 0.24f);
                    poleL = new Vector3(-1, -1, 0); poleR = new Vector3(1, -1, 0);
                    break;
                }
                case Role.Horn:
                    lt = new Vector3(0.0f, -0.12f, 0.24f); rt = new Vector3(0.25f, -0.22f, 0.12f);
                    break;
                case Role.Tuba:
                    lt = new Vector3(-0.15f, -0.05f, 0.22f); rt = new Vector3(0.05f, -0.12f, 0.26f);
                    break;
                case Role.Drums:
                {
                    float hit = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2f * (Conductor.IsPlaying ? Conductor.BeatsPerSecond : 0.2f)));
                    lt = new Vector3(-0.22f, -0.28f + hit * 0.06f, 0.36f); rt = new Vector3(0.3f, -0.08f + (1 - hit) * 0.06f, 0.4f);
                    break;
                }
                default: // Audience
                    if (Time.time < clapUntil)
                    {
                        float clap = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 4.2f));
                        float apart = 0.03f + clap * 0.1f;
                        lt = new Vector3(-apart, -0.08f, 0.3f); rt = new Vector3(apart, -0.08f, 0.3f);
                        poleL = new Vector3(-1, -1, -0.5f); poleR = new Vector3(1, -1, -0.5f);
                    }
                    else { lt = new Vector3(-0.13f, -0.44f, 0.22f); rt = new Vector3(0.13f, -0.44f, 0.22f); }
                    break;
            }
            SolveIK(shoulderL, lt, poleL, upperL, foreL, handL);
            SolveIK(shoulderR, rt, poleR, upperR, foreR, handR);
        }

        void PlaceBow(Vector3 hand, Vector3 contact, float length)
        {
            if (bow == null) return;
            var d = (contact - hand).normalized;
            bow.localPosition = hand + d * length * 0.5f;
            bow.localRotation = Quaternion.FromToRotation(Vector3.up, d);
            bow.localScale = new Vector3(0.008f, length * 0.5f, 0.008f);
        }
    }

    /// Shared musical clock that characters read from.
    public static class Conductor
    {
        public static bool IsPlaying;
        public static float BeatsPerSecond = 1.6f;
    }
}
