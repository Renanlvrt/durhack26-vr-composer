using System.Collections.Generic;
using UnityEngine;

namespace VRComposer
{
    public class VenueInfo
    {
        public VenueId id;
        public string title, subtitle, inspiredBy;
        public int instrument;   // General MIDI program
        public GameObject root;
        public readonly List<Musician> players = new List<Musician>();
        public readonly List<Musician> audience = new List<Musician>();
        public Color ambient;
    }

    /// Builds each performance venue around the user (user stands at the origin, facing +Z).
    /// Static architecture is combined with StaticBatchingUtility to keep draw calls low on Quest 2.
    public static class Venues
    {
        public static readonly VenueId[] All = { VenueId.GoldenHall, VenueId.RecitalRoom, VenueId.JazzClub, VenueId.Cathedral };

        public static (string title, string subtitle, string inspired, int inst) Describe(VenueId v) => v switch
        {
            VenueId.GoldenHall => ("Golden Concert Hall", "String orchestra · you conduct · huge, warm acoustics", "Inspired by Vienna's Musikverein", 48),
            VenueId.RecitalRoom => ("Recital Room", "String quartet · intimate wood-panelled hall", "Inspired by Wigmore Hall, London", 48),
            VenueId.JazzClub => ("Midnight Jazz Club", "Jazz combo · sax lead · late-night crowd", "Inspired by New York basement clubs", 66),
            VenueId.Cathedral => ("Cathedral Nave", "Brass quintet · long stone echo", "Inspired by St Paul's Cathedral, London", 56),
            _ => ("Studio", "", "", 0),
        };

        static GameObject P(PrimitiveType t, Transform p, string n, Vector3 pos, Vector3 s, Color c, Vector3 e = default, bool unlit = false) => VRComposer.Build.Prim(t, p, n, pos, s, c, e, unlit);

        static Light AddLight(Transform p, LightType type, Vector3 pos, Vector3 euler, Color c, float intensity, float range = 10f, float angle = 40f)
        {
            var go = new GameObject(type + " light");
            go.transform.SetParent(p, false);
            go.transform.localPosition = pos; go.transform.localRotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = type; l.color = c; l.intensity = intensity; l.range = range; l.spotAngle = angle;
            l.shadows = LightShadows.None;
            l.renderMode = type == LightType.Directional ? LightRenderMode.ForcePixel : LightRenderMode.Auto;
            return l;
        }

        public static VenueInfo Create(VenueId id, Transform stage)
        {
            var info = new VenueInfo { id = id };
            var d = Describe(id);
            info.title = d.title; info.subtitle = d.subtitle; info.inspiredBy = d.inspired; info.instrument = d.inst;
            info.root = new GameObject("Venue · " + d.title);
            info.root.transform.SetParent(stage, false);
            var stat = VRComposer.Build.Child(info.root.transform, "Static architecture", Vector3.zero).transform;
            var dyn = info.root.transform;
            switch (id)
            {
                case VenueId.GoldenHall: GoldenHall(info, stat, dyn); break;
                case VenueId.RecitalRoom: Recital(info, stat, dyn); break;
                case VenueId.JazzClub: Jazz(info, stat, dyn); break;
                case VenueId.Cathedral: Cathedral(info, stat, dyn); break;
            }
            StaticBatchingUtility.Combine(stat.gameObject);
            return info;
        }

        static readonly Vector3 User = Vector3.zero;

        // ------------------------------------------------------------------
        static void GoldenHall(VenueInfo info, Transform s, Transform d)
        {
            info.ambient = new Color(0.45f, 0.38f, 0.28f);
            Color gold = new Color(0.85f, 0.68f, 0.32f), cream = new Color(0.92f, 0.82f, 0.6f), wood = new Color(0.55f, 0.33f, 0.16f), red = new Color(0.5f, 0.08f, 0.1f);
            P(PrimitiveType.Cube, s, "Stage floor", new Vector3(0, -0.02f, 4), new Vector3(16, 0.04f, 14), wood);
            P(PrimitiveType.Cube, s, "Stalls floor", new Vector3(0, -0.03f, -10), new Vector3(16, 0.04f, 14), new Color(0.35f, 0.2f, 0.1f));
            P(PrimitiveType.Cube, s, "Wall L", new Vector3(-8, 6, 0), new Vector3(0.3f, 12, 34), cream);
            P(PrimitiveType.Cube, s, "Wall R", new Vector3(8, 6, 0), new Vector3(0.3f, 12, 34), cream);
            P(PrimitiveType.Cube, s, "Back wall", new Vector3(0, 6, 11), new Vector3(16, 12, 0.3f), cream);
            P(PrimitiveType.Cube, s, "Rear wall", new Vector3(0, 6, -17), new Vector3(16, 12, 0.3f), cream);
            P(PrimitiveType.Cube, s, "Ceiling", new Vector3(0, 12, 0), new Vector3(16, 0.3f, 34), gold * 0.8f);
            for (int side = -1; side <= 1; side += 2)
            {
                P(PrimitiveType.Cube, s, "Balcony", new Vector3(7f * side, 4.5f, -2), new Vector3(1.6f, 0.3f, 28), gold);
                P(PrimitiveType.Cube, s, "Balcony front", new Vector3(6.2f * side, 5.0f, -2), new Vector3(0.1f, 0.8f, 28), gold * 1.05f);
                for (int i = 0; i < 9; i++)
                {
                    P(PrimitiveType.Cylinder, s, "Caryatid", new Vector3(7.6f * side, 2.2f, -14 + i * 3.2f), new Vector3(0.35f, 2.2f, 0.35f), gold);
                    P(PrimitiveType.Cube, s, "Upper window", new Vector3(7.84f * side, 8.5f, -14 + i * 3.2f), new Vector3(0.05f, 2.4f, 1.2f), new Color(1f, 0.9f, 0.65f), default, true);
                }
            }
            // organ pipes behind the orchestra
            for (int i = 0; i < 22; i++) P(PrimitiveType.Cylinder, s, "Organ pipe", new Vector3(-4.2f + i * 0.4f, 6.5f + Mathf.Sin(i * 0.5f) * 0.8f, 10.7f), new Vector3(0.28f, 2.8f + Mathf.Sin(i * 0.5f) * 0.8f, 0.28f), new Color(0.82f, 0.8f, 0.75f));
            P(PrimitiveType.Cube, s, "Organ case", new Vector3(0, 4, 10.75f), new Vector3(10, 4, 0.4f), gold * 0.9f);
            // chandeliers
            for (int i = 0; i < 6; i++) P(PrimitiveType.Sphere, s, "Chandelier", new Vector3(i % 2 == 0 ? -3 : 3, 9.5f, -12 + (i / 2) * 9), Vector3.one * 0.9f, new Color(1f, 0.92f, 0.7f), default, true);
            // risers and podium
            P(PrimitiveType.Cube, s, "Riser", new Vector3(0, 0.15f, 6.2f), new Vector3(9, 0.3f, 3), wood * 0.9f);
            P(PrimitiveType.Cube, s, "Podium", new Vector3(0, 0.08f, 0.1f), new Vector3(0.9f, 0.16f, 0.9f), new Color(0.3f, 0.15f, 0.08f));
            // audience seats behind the conductor
            for (int r = 0; r < 6; r++) for (int c = -6; c <= 6; c++) P(PrimitiveType.Cube, s, "Seat", new Vector3(c * 0.62f, 0.25f, -3f - r * 1.0f), new Vector3(0.5f, 0.5f, 0.5f), red);

            AddLight(d, LightType.Directional, Vector3.zero, new Vector3(50, 20, 0), new Color(1f, 0.93f, 0.8f), 0.9f);
            // string orchestra: violins left, cellos right in two arcs around the podium, basses at the back
            int idx = 0;
            foreach (var (r, a0, a1, n, role) in new (float, float, float, int, Role)[] { (1.9f, -70, -8, 4, Role.Violin), (1.9f, 12, 70, 3, Role.Cello), (3.2f, -60, -12, 4, Role.Violin), (3.2f, 12, 55, 3, Role.Violin) })
                for (int i = 0; i < n; i++)
                {
                    float ang = Mathf.Lerp(a0, a1, n == 1 ? 0.5f : i / (float)(n - 1)) * Mathf.Deg2Rad;
                    var pos = new Vector3(Mathf.Sin(ang) * r, 0, Mathf.Cos(ang) * r + 0.2f);
                    info.players.Add(Musician.Create(d, role + " " + (i + 1), role, Outfit.ConcertBlack(idx++), pos, User, true, true));
                }
            for (int i = 0; i < 2; i++) info.players.Add(Musician.Create(d, "Double bass " + (i + 1), Role.DoubleBass, Outfit.ConcertBlack(idx++), new Vector3(2.6f + i * 0.9f, 0.3f, 5.2f), User, false, true));
            // first rows of the audience can applaud (behind you)
            for (int i = 0; i < 10; i++) info.audience.Add(Musician.Create(d, "Audience", Role.Audience, Outfit.Casual(i), new Vector3((i - 4.5f) * 0.62f, 0.05f, -3.1f - (i % 2) * 1.0f), new Vector3((i - 4.5f) * 0.62f, 0, 5), true, false));
        }

        // ------------------------------------------------------------------
        static void Recital(VenueInfo info, Transform s, Transform d)
        {
            info.ambient = new Color(0.42f, 0.33f, 0.24f);
            Color panel = new Color(0.5f, 0.32f, 0.18f), dark = new Color(0.32f, 0.2f, 0.1f), blue = new Color(0.35f, 0.45f, 0.6f);
            P(PrimitiveType.Cube, s, "Floor", new Vector3(0, -0.02f, 0), new Vector3(12, 0.04f, 18), dark);
            P(PrimitiveType.Cube, s, "Stage", new Vector3(0, 0.3f, 3.6f), new Vector3(7, 0.6f, 3.4f), panel);
            P(PrimitiveType.Cube, s, "Wall L", new Vector3(-6, 3.5f, 0), new Vector3(0.3f, 7, 18), panel);
            P(PrimitiveType.Cube, s, "Wall R", new Vector3(6, 3.5f, 0), new Vector3(0.3f, 7, 18), panel);
            P(PrimitiveType.Cube, s, "Back wall", new Vector3(0, 3.5f, 5.6f), new Vector3(12, 7, 0.3f), panel * 1.1f);
            P(PrimitiveType.Cube, s, "Ceiling", new Vector3(0, 7, 0), new Vector3(12, 0.3f, 18), dark);
            P(PrimitiveType.Sphere, s, "Cupola mural", new Vector3(0, 6.2f, 4.5f), new Vector3(5.5f, 3f, 2.2f), blue);
            for (int i = 0; i < 6; i++) for (int side = -1; side <= 1; side += 2) P(PrimitiveType.Cube, s, "Wood panel", new Vector3(5.82f * side, 2.8f, -7 + i * 2.4f), new Vector3(0.05f, 3.2f, 2f), panel * 1.15f);
            for (int r = 0; r < 7; r++) for (int c = -5; c <= 5; c++) if (!(r == 0 && Mathf.Abs(c) < 1)) P(PrimitiveType.Cube, s, "Seat", new Vector3(c * 0.6f, 0.22f, -0.2f - r * 0.95f), new Vector3(0.48f, 0.44f, 0.48f), new Color(0.45f, 0.1f, 0.12f));
            AddLight(d, LightType.Directional, Vector3.zero, new Vector3(45, -15, 0), new Color(1f, 0.9f, 0.75f), 0.75f);
            AddLight(d, LightType.Spot, new Vector3(0, 5.5f, 1.5f), new Vector3(60, 0, 0), new Color(1f, 0.92f, 0.8f), 2.2f, 9f, 55f);
            // quartet in a shallow arc facing the audience
            var quartet = new (Role, float)[] { (Role.Violin, -1.4f), (Role.Violin, -0.5f), (Role.Violin, 0.5f), (Role.Cello, 1.4f) };
            for (int i = 0; i < 4; i++)
            {
                var pos = new Vector3(quartet[i].Item2, 0.6f, 3.6f + Mathf.Abs(quartet[i].Item2) * -0.25f);
                info.players.Add(Musician.Create(d, i == 2 ? "Viola" : quartet[i].Item1.ToString(), quartet[i].Item1, Outfit.ConcertBlack(i + 5), pos, new Vector3(0, 0, 1.6f), true, true));
            }
            for (int i = 0; i < 8; i++) { float x = (i < 4 ? -1 : 1) * (0.6f + (i % 4) * 0.6f); info.audience.Add(Musician.Create(d, "Audience", Role.Audience, Outfit.Casual(i + 3), new Vector3(x, 0.02f, -0.2f), new Vector3(x, 0, 5), true, false)); }
        }

        // ------------------------------------------------------------------
        static void Jazz(VenueInfo info, Transform s, Transform d)
        {
            info.ambient = new Color(0.16f, 0.08f, 0.08f);
            Color velvet = new Color(0.5f, 0.04f, 0.07f), floor = new Color(0.18f, 0.1f, 0.06f), brick = new Color(0.3f, 0.14f, 0.1f);
            P(PrimitiveType.Cube, s, "Floor", new Vector3(0, -0.02f, 0), new Vector3(10, 0.04f, 12), floor);
            P(PrimitiveType.Cube, s, "Bandstand", new Vector3(0, 0.15f, 3.4f), new Vector3(5.5f, 0.3f, 2.6f), new Color(0.12f, 0.07f, 0.05f));
            for (int i = 0; i < 18; i++) P(PrimitiveType.Cylinder, s, "Curtain fold", new Vector3(-3.4f + i * 0.4f, 1.9f, 4.85f), new Vector3(0.42f, 1.9f, 0.25f), i % 2 == 0 ? velvet : velvet * 0.75f);
            P(PrimitiveType.Cube, s, "Wall L", new Vector3(-5, 2, 0), new Vector3(0.3f, 4, 12), brick);
            P(PrimitiveType.Cube, s, "Wall R", new Vector3(5, 2, 0), new Vector3(0.3f, 4, 12), brick);
            P(PrimitiveType.Cube, s, "Rear wall", new Vector3(0, 2, -6), new Vector3(10, 4, 0.3f), brick);
            P(PrimitiveType.Cube, s, "Ceiling", new Vector3(0, 4, 0), new Vector3(10, 0.2f, 12), new Color(0.06f, 0.05f, 0.05f));
            P(PrimitiveType.Cube, s, "Sign", new Vector3(0, 3.35f, 4.7f), new Vector3(2.4f, 0.55f, 0.05f), new Color(0.9f, 0.82f, 0.62f), default, true);
            VRComposer.Build.Text(d, "Sign text", "THE MIDNIGHT", new Vector3(0, 3.35f, 4.66f), new Vector2(2.1f, 0.35f), new Color(0.45f, 0.3f, 0.12f));
            // candlelit tables either side of you
            int a = 0;
            foreach (var tp in new[] { new Vector3(-1.8f, 0, 0.8f), new Vector3(1.8f, 0, 0.8f), new Vector3(-2.4f, 0, -1.2f), new Vector3(2.4f, 0, -1.2f), new Vector3(0, 0, -2.2f) })
            {
                P(PrimitiveType.Cylinder, s, "Table", tp + new Vector3(0, 0.72f, 0), new Vector3(0.7f, 0.02f, 0.7f), new Color(0.12f, 0.07f, 0.04f));
                P(PrimitiveType.Cylinder, s, "Table leg", tp + new Vector3(0, 0.36f, 0), new Vector3(0.08f, 0.36f, 0.08f), new Color(0.1f, 0.1f, 0.1f));
                P(PrimitiveType.Sphere, s, "Candle glow", tp + new Vector3(0, 0.8f, 0), Vector3.one * 0.07f, new Color(1f, 0.65f, 0.25f), default, true);
                for (int k = 0; k < 2; k++)
                {
                    var off = new Vector3(k == 0 ? -0.45f : 0.45f, 0, 0);
                    info.audience.Add(Musician.Create(d, "Guest", Role.Audience, Outfit.Casual(a++ + 7), tp + off, tp, true, false));
                }
            }
            AddLight(d, LightType.Directional, Vector3.zero, new Vector3(60, 0, 0), new Color(0.6f, 0.35f, 0.3f), 0.25f);
            AddLight(d, LightType.Spot, new Vector3(-1.5f, 3.8f, 1.5f), new Vector3(60, 20, 0), new Color(1f, 0.75f, 0.45f), 3f, 8f, 45f);
            AddLight(d, LightType.Spot, new Vector3(1.5f, 3.8f, 1.5f), new Vector3(60, -20, 0), new Color(0.85f, 0.55f, 1f), 2f, 8f, 45f);
            // the band: sax takes your melody; piano, bass and drums groove along
            info.players.Add(Musician.Create(d, "Saxophone (lead)", Role.Saxophone, Outfit.Jazz(0), new Vector3(0, 0.3f, 2.9f), User, false, true));
            info.players.Add(Musician.Create(d, "Pianist", Role.Piano, Outfit.Jazz(1), new Vector3(-1.8f, 0.3f, 3.0f), new Vector3(-3.6f, 0, 3.6f), true, false));
            info.players.Add(Musician.Create(d, "Double bass", Role.DoubleBass, Outfit.Jazz(2), new Vector3(1.2f, 0.3f, 3.9f), User, false, false));
            info.players.Add(Musician.Create(d, "Drums", Role.Drums, Outfit.Jazz(3), new Vector3(2.0f, 0.3f, 3.6f), new Vector3(0, 0, 1), true, false));
        }

        // ------------------------------------------------------------------
        static void Cathedral(VenueInfo info, Transform s, Transform d)
        {
            info.ambient = new Color(0.35f, 0.35f, 0.4f);
            Color stone = new Color(0.68f, 0.66f, 0.62f), dark = new Color(0.45f, 0.44f, 0.42f);
            P(PrimitiveType.Cube, s, "Nave floor", new Vector3(0, -0.02f, 8), new Vector3(14, 0.04f, 44), new Color(0.55f, 0.53f, 0.5f));
            for (int side = -1; side <= 1; side += 2)
            {
                P(PrimitiveType.Cube, s, "Outer wall", new Vector3(7 * side, 10, 8), new Vector3(0.4f, 20, 44), dark);
                for (int i = 0; i < 9; i++)
                {
                    float z = -10 + i * 4.2f;
                    P(PrimitiveType.Cylinder, s, "Column", new Vector3(3.6f * side, 5, z), new Vector3(0.9f, 5, 0.9f), stone);
                    P(PrimitiveType.Cube, s, "Arch", new Vector3(3.6f * side, 10.6f, z + 2.1f), new Vector3(0.7f, 0.9f, 3.6f), stone);
                    var c = new[] { new Color(0.8f, 0.15f, 0.2f), new Color(0.2f, 0.35f, 0.85f), new Color(0.95f, 0.75f, 0.2f), new Color(0.2f, 0.6f, 0.35f) }[i % 4];
                    P(PrimitiveType.Cube, s, "Stained glass", new Vector3(6.75f * side, 9f, z + 2.1f), new Vector3(0.05f, 5f, 1.6f), c, default, true);
                }
            }
            P(PrimitiveType.Cube, s, "Vault", new Vector3(0, 19.5f, 8), new Vector3(14, 0.5f, 44), dark);
            P(PrimitiveType.Cube, s, "East wall", new Vector3(0, 10, 30), new Vector3(14, 20, 0.4f), dark);
            // rose window
            P(PrimitiveType.Cylinder, s, "Rose window", new Vector3(0, 12, 29.7f), new Vector3(6, 0.05f, 6), new Color(0.2f, 0.2f, 0.35f), new Vector3(90, 0, 0), true);
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.PI / 6f;
                var c = new[] { new Color(0.85f, 0.15f, 0.2f), new Color(0.2f, 0.4f, 0.9f), new Color(1f, 0.8f, 0.2f), new Color(0.2f, 0.7f, 0.4f) }[k % 4];
                P(PrimitiveType.Sphere, s, "Rose glass", new Vector3(Mathf.Cos(a) * 2f, 12 + Mathf.Sin(a) * 2f, 29.6f), new Vector3(0.9f, 0.9f, 0.05f), c, default, true);
            }
            for (int r = 0; r < 6; r++) for (int side = -1; side <= 1; side += 2) P(PrimitiveType.Cube, s, "Pew", new Vector3(1.6f * side, 0.45f, -1.5f - r * 1.2f), new Vector3(2.4f, 0.9f, 0.45f), new Color(0.32f, 0.18f, 0.09f));
            for (int i = 0; i < 6; i++) P(PrimitiveType.Cylinder, s, "Candle", new Vector3(-2.5f + i, 1.1f, 6), new Vector3(0.06f, 0.3f, 0.06f), new Color(1f, 0.95f, 0.85f), default, true);
            AddLight(d, LightType.Directional, Vector3.zero, new Vector3(35, -30, 0), new Color(0.95f, 0.9f, 0.85f), 0.8f);
            // brass quintet: trumpets and trombone stand, horn and tuba sit
            var q = new (Role role, float x, bool sit)[] { (Role.Trumpet, -1.6f, false), (Role.Trumpet, -0.8f, false), (Role.Horn, 0f, true), (Role.Trombone, 0.8f, false), (Role.Tuba, 1.6f, true) };
            for (int i = 0; i < q.Length; i++)
                info.players.Add(Musician.Create(d, q[i].role.ToString(), q[i].role, Outfit.ConcertBlack(i + 11), new Vector3(q[i].x, 0, 3.0f - Mathf.Abs(q[i].x) * 0.3f), User, q[i].sit, i == 0 || i == 1 || i == 3));
            for (int i = 0; i < 8; i++) { float x = (i < 4 ? -1 : 1) * (0.7f + (i % 4) * 0.55f); info.audience.Add(Musician.Create(d, "Audience", Role.Audience, Outfit.Casual(i + 21), new Vector3(x, 0.02f, -1.9f), new Vector3(x, 0, 5), true, false)); }
        }
    }
}
