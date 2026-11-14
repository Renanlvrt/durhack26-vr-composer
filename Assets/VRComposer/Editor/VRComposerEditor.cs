using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRComposer.EditorTools
{
    /// <summary>Menu: Tools > VR Composer > Add To Open Scene.</summary>
    [InitializeOnLoad]
    public static class VRComposerMenu
    {
        private const string GlyphFolder = "Assets/VRComposer/Resources/VRComposerGlyphs";
        private const string MaterialFolder = "Assets/VRComposer/Resources/VRComposerMaterials";

        static VRComposerMenu()
        {
            EditorApplication.delayCall += EnsureMaterials;
        }

        /// <summary>
        /// URP materials for the venues. Kept in Resources so the URP shaders are included in the
        /// Quest build (Shader.Find alone would strip them and the venues would render pink).
        /// </summary>
        [MenuItem("Tools/VR Composer/Create Venue Materials")]
        public static void EnsureMaterials()
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/VRComposer/Resources", "VRComposerMaterials");
            Make(MaterialFolder + "/VRC_Lit.mat", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Lit");
            Make(MaterialFolder + "/VRC_Unlit.mat", "Universal Render Pipeline/Unlit", "Sprites/Default");
        }

        private static void Make(string path, string shader, string fallback)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var sh = Shader.Find(shader);
            if (sh == null) sh = Shader.Find(fallback);
            if (sh == null) { Debug.LogWarning("[VRComposer] Shader not found: " + shader); return; }
            var m = new Material(sh) { enableInstancing = true };
            AssetDatabase.CreateAsset(m, path);
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Tools/VR Composer/Add To Open Scene")]
        public static void AddToScene()
        {
            var existing = Object.FindAnyObjectByType<VRComposerApp>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[VRComposer] Already in the scene.");
                return;
            }

            ReimportGlyphs();
            EnsureMaterials();

            var go = new GameObject("VR Composer");
            go.AddComponent<ScorePlayer>();
            go.AddComponent<VRComposerApp>();
            Undo.RegisterCreatedObjectUndo(go, "Add VR Composer");
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);

            if (Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>() == null)
                Debug.LogWarning("[VRComposer] No XR Origin in this scene. Use SampleScene (it has the XR rig), " +
                                 "or add the 'XR Origin (XR Rig)' prefab from the XR Interaction Toolkit Starter Assets.");
        }

        [MenuItem("Tools/VR Composer/Reimport Glyph Textures")]
        public static void ReimportGlyphs()
        {
            AssetDatabase.ImportAsset(GlyphFolder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
        }
    }

    /// <summary>
    /// Imports the music glyph PNGs as clean UI textures. Mipmaps + trilinear keep the thin
    /// lines of the glyphs from shimmering when the panels are seen at an angle in the headset.
    /// </summary>
    public class VRComposerGlyphImporter : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains("/VRComposerGlyphs/")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = true;
            ti.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
        }
    }
}
