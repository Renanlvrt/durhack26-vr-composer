using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VRComposer
{
    public readonly struct SGlyph
    {
        public readonly string name;
        public readonly Vector2 sw, ne;
        public SGlyph(string n, float x0, float y0, float x1, float y1) { name = n; sw = new Vector2(x0, y0); ne = new Vector2(x1, y1); }
        public Vector2 Size { get { return ne - sw; } }
        public Vector2 Centre { get { return (sw + ne) * 0.5f; } }
    }

    /// <summary>Engraving defaults from Leland's SMuFL metadata (units: stave spaces).</summary>
    public static class Smufl
    {
        public const float StaffLineThickness = 0.11f;
        public const float StemThickness = 0.12f;
        public const float LegerLineThickness = 0.16f;
        public const float LegerLineExtension = 0.33f;
        public const float ThinBarlineThickness = 0.18f;
        public const float ThickBarlineThickness = 0.55f;
        public const float ThinThickBarlineSeparation = 0.37f;
        public static readonly Vector2 StemUpSE = new Vector2(1.3f, 0.16f), StemDownNW = new Vector2(0f, -0.168f);

        public static SGlyph G(string name)
        {
            SGlyph g;
            return LelandGlyphs.All.TryGetValue(name, out g) ? g : new SGlyph(name, 0, -0.5f, 1, 0.5f);
        }
    }

    /// <summary>Leland glyph textures (Resources/VRComposerGlyphs), white on transparent; tint with Image.color.</summary>
    public static class Glyphs
    {
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

        public static Sprite Get(string name)
        {
            Sprite s;
            if (cache.TryGetValue(name, out s) && s != null) return s;
            var tex = Resources.Load<Texture2D>("VRComposerGlyphs/" + name);
            if (tex == null) { Debug.LogWarning("[VRComposer] Missing glyph texture: " + name); s = UIFactory.Rounded; }
            else s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            cache[name] = s;
            return s;
        }

        public static string NoteFlag(Duration d, bool up)
        {
            if (d == Duration.Eighth) return up ? "flag8thUp" : "flag8thDown";
            if (d == Duration.Sixteenth) return up ? "flag16thUp" : "flag16thDown";
            return null;
        }

        public static string Rest(Duration d)
        {
            switch (d)
            {
                case Duration.Whole: return "restWhole";
                case Duration.Half: return "restHalf";
                case Duration.Quarter: return "restQuarter";
                case Duration.Eighth: return "rest8th";
                default: return "rest16th";
            }
        }

        public static string Symbol(SymbolKind k)
        {
            switch (k)
            {
                case SymbolKind.Sharp: return "accidentalSharp";
                case SymbolKind.Flat: return "accidentalFlat";
                case SymbolKind.Natural: return "accidentalNatural";
                case SymbolKind.Dot: return "augmentationDot";
                case SymbolKind.Staccato: return "articStaccatoAbove";
                case SymbolKind.Accent: return "articAccentAbove";
                case SymbolKind.Tie: return "tieArc";
                case SymbolKind.Fermata: return "fermataAbove";
                case SymbolKind.Pianissimo: return "dynamicPP";
                case SymbolKind.Piano: return "dynamicPiano";
                case SymbolKind.Forte: return "dynamicForte";
                case SymbolKind.Fortissimo: return "dynamicFF";
                case SymbolKind.Sforzando: return "dynamicSforzando1";
                case SymbolKind.Crescendo: return null;          // drawn as the word "cresc."
                case SymbolKind.TrebleClef: return "gClef";
                case SymbolKind.AltoClef: return "cClef";
                case SymbolKind.BassClef: return "fClef";
                default: return null;
            }
        }

        public static string Dynamic(int level)
        {
            switch (level)
            {
                case -2: return "dynamicPP";
                case -1: return "dynamicPiano";
                case 1: return "dynamicForte";
                case 2: return "dynamicFF";
                default: return null;
            }
        }
    }

    /// <summary>
    /// Draws notation with uGUI Images placed from SMuFL metrics. All coordinates are in the
    /// parent's local pixels, y up, relative to the parent's centre (anchors at 0.5, 0.5).
    /// ls = one stave space in pixels.
    /// </summary>
    public static class Engraver
    {
        public static RectTransform Container(Transform parent, string name, Vector2 anchoredPos)
        {
            var rt = UIFactory.Rect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = anchoredPos;
            return rt;
        }

        /// <summary>Glyph with its SMuFL origin at (ox, oy) relative to the parent's centre.</summary>
        public static Image Glyph(Transform parent, string glyph, float ox, float oy, float ls, Color c)
        {
            var g = Smufl.G(glyph);
            var img = UIFactory.Img(parent, glyph, c, Glyphs.Get(glyph));
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(ox + g.sw.x * ls, oy + g.sw.y * ls);
            rt.sizeDelta = g.Size * ls;
            return img;
        }

        /// <summary>Glyph centred on (cx, cy).</summary>
        public static Image GlyphCentred(Transform parent, string glyph, float cx, float cy, float ls, Color c)
        {
            var g = Smufl.G(glyph);
            return Glyph(parent, glyph, cx - g.Centre.x * ls, cy - g.Centre.y * ls, ls, c);
        }

        /// <summary>Filled rectangle centred on (cx, cy).</summary>
        public static Image Box(Transform parent, string name, float cx, float cy, float w, float h, Color c)
        {
            var img = UIFactory.Img(parent, name, c);
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cx, cy);
            rt.sizeDelta = new Vector2(w, h);
            return img;
        }

        /// <summary>
        /// A note or rest. Origin = notehead centre (notes) or bottom stave line (rests).
        /// headStep = the note's stave step, used for ledger lines and stem direction.
        /// </summary>
        public static void Note(Transform r, Note n, float ls, Color c, bool decorations = true)
        {
            if (n.isRest) { Rest(r, n, ls, c); return; }

            string head = n.duration == Duration.Whole ? "noteheadWhole" : n.duration == Duration.Half ? "noteheadHalf" : "noteheadBlack";
            var hg = Smufl.G(head);
            float w = hg.ne.x - 0.03f;                 // drawn width without padding
            float left = -w / 2f;
            Glyph(r, head, left * ls, 0, ls, c);

            // ledger lines
            float lw = (w + 2 * Smufl.LegerLineExtension) * ls, lt = Mathf.Max(1.5f, Smufl.LegerLineThickness * ls);
            for (int k = -2; k >= n.step; k -= 2) Box(r, "Ledger", 0, (k - n.step) * ls / 2f, lw, lt, c);
            for (int k = 10; k <= n.step; k += 2) Box(r, "Ledger", 0, (k - n.step) * ls / 2f, lw, lt, c);

            bool up = n.step < 4;
            float lowest = -0.6f;
            for (int k = -2; k >= n.step; k -= 2) lowest = Mathf.Min(lowest, (k - n.step) * 0.5f - 0.2f);

            if (n.duration != Duration.Whole)
            {
                float len = Mathf.Max(3.5f, Mathf.Abs(4 - n.step) * 0.5f);
                if (n.duration == Duration.Sixteenth) len += 0.25f;
                float t = Smufl.StemThickness;
                string flag = Glyphs.NoteFlag(n.duration, up);
                if (up)
                {
                    float x1 = left + Smufl.StemUpSE.x, x0 = x1 - t, y0 = Smufl.StemUpSE.y, y1 = len;
                    Box(r, "Stem", (x0 + x1) / 2 * ls, (y0 + y1) / 2 * ls, Mathf.Max(1.5f, t * ls), (y1 - y0) * ls, c);
                    if (flag != null) Glyph(r, flag, x0 * ls, y1 * ls, ls, c);
                }
                else
                {
                    float x0 = left + Smufl.StemDownNW.x, x1 = x0 + t, y0 = Smufl.StemDownNW.y, y1 = -len;
                    lowest = Mathf.Min(lowest, y1);
                    Box(r, "Stem", (x0 + x1) / 2 * ls, (y0 + y1) / 2 * ls, Mathf.Max(1.5f, t * ls), (y0 - y1) * ls, c);
                    if (flag != null) Glyph(r, flag, x0 * ls, y1 * ls, ls, c);
                }
            }

            if (n.dotted)
            {
                float dy = (Score.Mod(n.step, 2) == 0) ? 0.5f : 0f;   // dots sit in a space
                Glyph(r, "augmentationDot", (w / 2f + 0.3f) * ls, dy * ls, ls, c);
            }
            if (!decorations) return;

            if (n.accidental != 0 || n.natural)
            {
                string acc = n.accidental > 0 ? "accidentalSharp" : n.accidental < 0 ? "accidentalFlat" : "accidentalNatural";
                var ag = Smufl.G(acc);
                Glyph(r, acc, (left - 0.25f - ag.ne.x) * ls, 0, ls, c);
            }

            // articulations: notehead side (opposite the stem), in a space when inside the stave
            int dir = up ? -1 : 1;
            int markStep = n.step + dir * 2;
            if (markStep >= 0 && markStep <= 8 && Score.Mod(markStep, 2) == 0) markStep += dir;
            if (n.staccato)
            {
                string g = up ? "articStaccatoBelow" : "articStaccatoAbove";
                float cy = (markStep - n.step) * 0.5f;
                GlyphCentred(r, g, 0, cy * ls, ls, c);
                if (up) lowest = Mathf.Min(lowest, cy - 0.3f);
                markStep += dir * 2;
            }
            if (n.accent)
            {
                string g = up ? "articAccentBelow" : "articAccentAbove";
                float cy = (markStep - n.step) * 0.5f;
                GlyphCentred(r, g, 0, cy * ls, ls, c);
                if (up) lowest = Mathf.Min(lowest, cy - 0.6f);
            }
            float stemTop = up && n.duration != Duration.Whole ? Mathf.Max(3.5f, Mathf.Abs(4 - n.step) * 0.5f) : 0.6f;
            if (n.fermata)
            {
                float staffTop = (8 - n.step) * 0.5f;
                Fermata(r, Mathf.Max(staffTop + 0.9f, stemTop + 0.5f) * ls, ls, c);
            }
            Dynamics(r, n, Mathf.Min(-n.step * 0.5f - 2.3f, lowest - 1.3f) * ls, ls, c);
        }

        /// <summary>Palette / drag icon for a symbol, centred on (0, 0).</summary>
        public static void SymbolIcon(Transform r, SymbolKind k, float ls, Color c)
        {
            if (k == SymbolKind.Tie)
            {
                var head = Smufl.G("noteheadBlack");
                GlyphCentred(r, "noteheadBlack", -1.3f * ls, 0, ls, c);
                GlyphCentred(r, "noteheadBlack", 1.3f * ls, 0, ls, c);
                var t = Tie(r, 0, 0, 0, false, ls, c);
                var rt = t.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(2.4f * ls, 0.7f * ls);
                rt.anchoredPosition = new Vector2(-1.2f * ls, -0.6f * ls);
                return;
            }
            string g = Glyphs.Symbol(k);
            if (g != null) { GlyphCentred(r, g, 0, 0, ls, c); return; }
            var txt = UIFactory.Text(r, k == SymbolKind.Crescendo ? "cresc." : k.ToString(), 1.6f * ls, c, TextAlignmentOptions.Center, FontStyles.Italic);
            var tr = txt.rectTransform;
            tr.anchorMin = tr.anchorMax = new Vector2(0.5f, 0.5f);
            tr.sizeDelta = new Vector2(6f * ls, 2f * ls);
            tr.anchoredPosition = Vector2.zero;
        }

        /// <summary>Fermata centred over x = 0, its base at y.</summary>
        public static void Fermata(Transform r, float y, float ls, Color c)
        {
            var g = Smufl.G("fermataAbove");
            Glyph(r, "fermataAbove", -g.Centre.x * ls, y, ls, c);
        }

        /// <summary>Dynamic, sf and "cresc."/"dim." under the note, centred on the line y.</summary>
        public static void Dynamics(Transform r, Note n, float y, float ls, Color c)
        {
            float x = 0;
            string d = Glyphs.Dynamic(n.dynamic);
            if (d != null) { var g = Smufl.G(d); GlyphCentred(r, d, 0, y, ls, c); x = g.Size.x * 0.5f * ls + 0.4f * ls; }
            if (n.sforzando)
            {
                var g = Smufl.G("dynamicSforzando1");
                float cx = d != null ? x + g.Size.x * 0.5f * ls : 0;
                GlyphCentred(r, "dynamicSforzando1", cx, y, ls, c);
                x = cx + g.Size.x * 0.5f * ls + 0.4f * ls;
            }
            if (n.hairpin != 0)
            {
                var t = UIFactory.Text(r, n.hairpin > 0 ? "cresc." : "dim.", 1.25f * ls, c, TextAlignmentOptions.Left, FontStyles.Italic);
                t.name = "Cresc";
                var rt = t.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(x > 0 ? x : -0.6f * ls, y);
                rt.sizeDelta = new Vector2(5f * ls, 1.6f * ls);
            }
        }

        /// <summary>A tie arc from x0 to x1 (parent-local px); its ends at y, bulging up or down.</summary>
        public static Image Tie(Transform r, float x0, float x1, float y, bool above, float ls, Color c)
        {
            float w = Mathf.Max(0.8f * ls, x1 - x0);
            float h = Mathf.Clamp(w * 0.18f, 0.45f * ls, 0.85f * ls);
            var img = UIFactory.Img(r, "Tie", c, Glyphs.Get("tieArc"));
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x0, y);
            if (!above) rt.localScale = new Vector3(1f, -1f, 1f);
            return img;
        }

        /// <summary>Rest; the parent's origin is the bottom stave line at the rest's x.</summary>
        public static void Rest(Transform r, Note n, float ls, Color c)
        {
            string g = Glyphs.Rest(n.duration);
            float originLine = n.duration == Duration.Whole ? 3f : 2f;   // whole rest hangs from the 4th line
            var gg = Smufl.G(g);
            Glyph(r, g, -gg.Centre.x * ls, originLine * ls, ls, c);
            if (n.dotted) Glyph(r, "augmentationDot", (gg.Size.x / 2f + 0.3f) * ls, 2.5f * ls, ls, c);
            if (n.fermata) Fermata(r, 4.9f * ls, ls, c);
            Dynamics(r, n, -2.3f * ls, ls, c);
        }

        public static void Recolour(GameObject go, Color c)
        {
            if (go == null) return;
            foreach (var g in go.GetComponentsInChildren<Graphic>(true))
                if (g.raycastTarget == false && g.name != "Halo") g.color = c;
        }
    }
}
