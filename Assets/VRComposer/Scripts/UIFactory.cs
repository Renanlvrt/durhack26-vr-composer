using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace VRComposer
{
    /// <summary>Colours, sprites and small builders for the runtime-built world-space UI (durhack.4 style).</summary>
    public static class UIFactory
    {
        public const float MetersPerPixel = 0.001f; // 1 canvas px = 1 mm
        public const int UILayer = 5;

        // durhack.4 palette: paper stave, near-black ink, white tiles on dark glass, orange = action, cyan = hover
        public static readonly Color Glass = new Color(0.13f, 0.13f, 0.19f, 0.86f);
        public static readonly Color GlassLight = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Paper = new Color(0.97f, 0.96f, 0.93f, 0.97f);
        public static readonly Color Ink = new Color(0.08f, 0.08f, 0.10f, 1f);
        public static readonly Color InkSoft = new Color(0.08f, 0.08f, 0.10f, 0.45f);
        public static readonly Color Tile = new Color(1f, 1f, 1f, 0.92f);
        public static readonly Color TileHover = new Color(1f, 0.93f, 0.85f, 1f);
        public static readonly Color Orange = new Color(0.95f, 0.42f, 0.12f, 1f);
        public static readonly Color OrangeSoft = new Color(0.95f, 0.42f, 0.12f, 0.22f);
        public static readonly Color Cyan = new Color(0.10f, 0.70f, 0.82f, 1f);
        public static readonly Color Green = new Color(0.25f, 0.70f, 0.35f, 1f);
        public static readonly Color Red = new Color(0.85f, 0.25f, 0.25f, 1f);
        public static readonly Color Dark = new Color(0.20f, 0.20f, 0.26f, 1f);
        public static readonly Color Indigo = new Color(0.25f, 0.30f, 0.55f, 1f);
        public static readonly Color TextLight = new Color(0.96f, 0.96f, 0.98f, 1f);
        public static readonly Color TextDim = new Color(1f, 1f, 1f, 0.62f);

        // kept for older call sites
        public static Color Accent { get { return Orange; } }
        public static Color PanelBg { get { return Glass; } }
        public static Color PanelBg2 { get { return Dark; } }

        private static Sprite rounded;

        /// <summary>Procedural 9-sliced rounded rectangle.</summary>
        public static Sprite Rounded
        {
            get
            {
                if (rounded != null) return rounded;
                const int size = 64, r = 20;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, r, size - r);
                        float cy = Mathf.Clamp(y + 0.5f, r, size - r);
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255);
                        px[y * size + x] = new Color32(255, 255, 255, a);
                    }
                tex.SetPixels32(px);
                tex.Apply();
                rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect, new Vector4(r, r, r, r));
                return rounded;
            }
        }

        public static Canvas WorldCanvas(string name, Vector2 sizePx, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UILayer;
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = sizePx;
            rt.localScale = Vector3.one * MetersPerPixel;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            go.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays + poke
            return canvas;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = UILayer;
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Anchors at the parent's top-left; position is (x right, y down-negative) in px.</summary>
        public static RectTransform PlaceTopLeft(RectTransform rt, float x, float y, float w, float h, Vector2? pivot = null)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = pivot ?? new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        public static Image Img(Transform parent, string name, Color color, Sprite sprite = null, bool raycast = false)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = raycast;
            if (sprite == Rounded) img.type = Image.Type.Sliced;
            return img;
        }

        /// <summary>An invisible but raycastable image (alpha 1/255 so it is never culled).</summary>
        public static Image HitArea(Transform parent, string name)
        {
            var img = Img(parent, name, new Color(0f, 0f, 0f, 1f / 255f), null, true);
            return img;
        }

        public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Center, FontStyles style = FontStyles.Normal)
        {
            var rt = Rect("Text", parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        /// <summary>Dark glass panel background with a title.</summary>
        public static RectTransform GlassPanel(Canvas c, string title)
        {
            var root = (RectTransform)c.transform;
            var bg = Img(root, "Glass", Glass, Rounded);
            Stretch(bg.rectTransform);
            if (!string.IsNullOrEmpty(title))
            {
                var t = Text(root, title, 26, TextLight, TextAlignmentOptions.Left, FontStyles.Bold);
                PlaceTopLeft(t.rectTransform, 22, -14, root.sizeDelta.x - 44, 36);
            }
            return root;
        }

        public static Button Button(Transform parent, string label, Action onClick, Color? bg = null, float fontSize = 24)
        {
            var img = Img(parent, "Button " + label, bg ?? Dark, Rounded, true);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            cb.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            cb.selectedColor = Color.white;
            cb.colorMultiplier = 1.3f;
            cb.fadeDuration = 0.05f;
            btn.colors = cb;
            var nav = btn.navigation;
            nav.mode = Navigation.Mode.None;
            btn.navigation = nav;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            img.gameObject.AddComponent<HoverFeedback>();

            var t = Text(img.transform, label, fontSize, TextLight);
            Stretch(t.rectTransform, 8, 0, 8, 0);
            t.name = "Label";
            return btn;
        }

        public static void SetButton(Button b, string label, Color colour)
        {
            if (b == null) return;
            var l = LabelOf(b);
            if (l != null && l.text != label) l.text = label;
            var img = b.targetGraphic as Image;
            if (img != null && img.color != colour) img.color = colour;
        }

        public static TextMeshProUGUI LabelOf(Button b) { return b.GetComponentInChildren<TextMeshProUGUI>(); }

        public static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var c = t.GetChild(i).gameObject;
                c.SetActive(false);
                UnityEngine.Object.Destroy(c);
            }
        }

        /// <summary>Short controller buzz for the interactor behind a UI event (no-op for mouse).</summary>
        public static void Buzz(BaseEventData e, float amplitude, float duration)
        {
            var td = e as TrackedDeviceEventData;
            if (td == null) return;
            var input = td.interactor as XRBaseInputInteractor;
            if (input != null) input.SendHapticImpulse(amplitude, duration);
        }
    }

    /// <summary>Hover feedback for tiles and buttons: slight grow and a light haptic tick.</summary>
    public class HoverFeedback : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public float scale = 1.05f;
        public Image tint;
        public Color normal, hover;
        public bool useTint;

        public void OnPointerEnter(PointerEventData e)
        {
            transform.localScale = Vector3.one * scale;
            if (useTint && tint != null) tint.color = hover;
            UIFactory.Buzz(e, 0.08f, 0.01f);
        }

        public void OnPointerExit(PointerEventData e)
        {
            transform.localScale = Vector3.one;
            if (useTint && tint != null) tint.color = normal;
        }

        private void OnDisable() { transform.localScale = Vector3.one; }
    }
}
