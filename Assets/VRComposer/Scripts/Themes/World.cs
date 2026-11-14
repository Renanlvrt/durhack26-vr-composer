using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace VRComposer
{
    /// <summary>
    /// URP materials for the venues. Uses the materials created by the editor tool in
    /// Resources/VRComposerMaterials (so the shaders are kept in Quest builds), falling back to
    /// Shader.Find in the editor.
    /// </summary>
    public static class Mats
    {
        private static readonly Dictionary<Color, Material> lit = new Dictionary<Color, Material>();
        private static readonly Dictionary<Color, Material> unlit = new Dictionary<Color, Material>();
        private static Material litTemplate, unlitTemplate;

        private static Material Template(bool isLit)
        {
            if (isLit && litTemplate != null) return litTemplate;
            if (!isLit && unlitTemplate != null) return unlitTemplate;
            var m = Resources.Load<Material>(isLit ? "VRComposerMaterials/VRC_Lit" : "VRComposerMaterials/VRC_Unlit");
            if (m == null)
            {
                var sh = Shader.Find(isLit ? "Universal Render Pipeline/Simple Lit" : "Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find(isLit ? "Universal Render Pipeline/Lit" : "Sprites/Default");
                m = new Material(sh);
            }
            if (isLit) litTemplate = m; else unlitTemplate = m;
            return m;
        }

        private static Material Make(Dictionary<Color, Material> cache, Color c, bool isLit)
        {
            Material m;
            if (cache.TryGetValue(c, out m) && m != null) return m;
            m = new Material(Template(isLit));
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.enableInstancing = true;
            cache[c] = m;
            return m;
        }

        public static Material Lit(Color c) { return Make(lit, c, true); }
        public static Material Get(Color c) { return Make(unlit, c, false); }
    }

    /// <summary>Primitive builders for venues and characters (ported from durhack.4).</summary>
    public static class Build
    {
        /// <summary>A lit primitive (sphere, capsule, cube, cylinder) without a collider.</summary>
        public static GameObject Prim(PrimitiveType type, Transform parent, string name, Vector3 pos, Vector3 scale, Color c, Vector3 euler = default(Vector3), bool unlit = false)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = unlit ? Mats.Get(c) : Mats.Lit(c);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        public static GameObject Child(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go;
        }

        /// <summary>3D text fitted roughly to a box (metres).</summary>
        public static TextMeshPro Text(Transform parent, string name, string text, Vector3 centre, Vector2 box, Color c)
        {
            var go = Child(parent, name, centre);
            go.transform.localRotation = Quaternion.identity;
            var t = go.AddComponent<TextMeshPro>();
            t.text = text;
            t.color = c;
            t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.1f;
            t.fontSizeMax = 50f;
            t.rectTransform.sizeDelta = box;
            t.fontStyle = FontStyles.Bold;
            // TMP text reads correctly for a viewer looking along +Z, which is how venues face the user.
            return t;
        }
    }
}
