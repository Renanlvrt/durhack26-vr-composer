using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace VRComposer
{
    /// <summary>
    /// GraphicRaycaster for the mouse only. The panels also have a TrackedDeviceGraphicRaycaster for
    /// the XR rays; this one ignores XR pointer events so a controller ray is never counted twice.
    /// </summary>
    public class MouseGraphicRaycaster : GraphicRaycaster
    {
        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
            if (eventData is TrackedDeviceEventData) return;
            base.Raycast(eventData, resultAppendList);
        }
    }

    /// <summary>
    /// Test the app without a headset (Play in the Unity editor with Quest Link off).
    /// - The mouse clicks and drags on every panel, in VR too (game view).
    /// - With no headset: hold the right mouse button to look around, WASD to move, Q/E down/up,
    ///   Shift = faster. The keys that already worked still do: Space play/stop, R recenter the
    ///   panels in front of you, arrows nudge the selected note, Esc deselect. H hides this help.
    /// </summary>
    public class DesktopMode : MonoBehaviour
    {
        public bool forceDesktop;              // set from VRComposerApp
        public float eyeHeight = 1.6f;
        public float moveSpeed = 1.5f;
        public float lookSpeed = 0.15f;

        public bool Active { get; private set; }

        private Transform cam;
        private float yaw, pitch;
        private bool showHelp = true;
        private float checkUntil;
        private GUIStyle style;

        /// <summary>Lets the XR UI module read the mouse (the template scene has it turned off).</summary>
        public static void EnableMouseUI()
        {
            var es = EventSystem.current != null ? EventSystem.current : FindAnyObjectByType<EventSystem>();
            if (es == null) return;
            var xr = es.GetComponent<XRUIInputModule>();
            if (xr != null) xr.enableMouseInput = true;
        }

        private void Start()
        {
            EnableMouseUI();
            checkUntil = Time.unscaledTime + 3f;   // the headset can take a moment to start
        }

        private static bool HeadsetActive()
        {
            var displays = new List<UnityEngine.XR.XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var d in displays) if (d.running) return true;
            return false;
        }

        private void Update()
        {
            if (!Active)
            {
                if (forceDesktop || (Time.unscaledTime > checkUntil && !HeadsetActive())) Activate();
                else return;
            }
            if (cam == null) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                yaw += d.x * lookSpeed;
                pitch = Mathf.Clamp(pitch - d.y * lookSpeed, -80f, 80f);
            }
            cam.rotation = Quaternion.Euler(pitch, yaw, 0f);

            if (kb != null)
            {
                Vector3 move = Vector3.zero;
                Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized, right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
                if (kb.wKey.isPressed) move += fwd;
                if (kb.sKey.isPressed) move -= fwd;
                if (kb.dKey.isPressed) move += right;
                if (kb.aKey.isPressed) move -= right;
                if (kb.eKey.isPressed) move += Vector3.up;
                if (kb.qKey.isPressed) move -= Vector3.up;
                float speed = moveSpeed * (kb.leftShiftKey.isPressed ? 3f : 1f);
                cam.position += move * speed * Time.unscaledDeltaTime;
                if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
            }
        }

        private void Activate()
        {
            Active = true;
            var c = Camera.main;
            if (c == null) { Debug.LogWarning("[VRComposer] Desktop mode: no MainCamera."); return; }
            cam = c.transform;
            // the tracked pose driver would hold the camera where the (absent) headset is
            foreach (var b in c.GetComponents<Behaviour>())
                if (b != null && b.GetType().Name == "TrackedPoseDriver") b.enabled = false;
            var p = cam.position;
            if (p.y < 0.5f) cam.position = new Vector3(p.x, p.y + eyeHeight, p.z);
            yaw = cam.eulerAngles.y;
            pitch = 0f;
            cam.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (c.fieldOfView < 50f) c.fieldOfView = 70f;
            Debug.Log("[VRComposer] No headset: desktop mode (right mouse = look, WASD = move, click and drag the panels).");
            var app = VRComposerApp.Instance;
            if (app != null) app.Recenter();
        }

        private void OnGUI()
        {
            if (!Active || !showHelp) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };
                style.normal.textColor = Color.white;
                style.padding = new RectOffset(10, 10, 8, 8);
            }
            GUI.Box(new Rect(10, 10, 360, 150),
                "Desktop mode (no headset)\n" +
                "Click / drag: use the panels like the VR ray\n" +
                "Right mouse + move: look around\n" +
                "WASD move · Q/E down/up · Shift faster\n" +
                "Space play/stop · R panels in front of you\n" +
                "Arrows nudge the selected note · Esc deselect\n" +
                "H hide this help", style);
        }
    }
}
