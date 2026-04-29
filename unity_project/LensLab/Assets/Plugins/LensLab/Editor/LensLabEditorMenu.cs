using LensLab.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Editor
{
    /// <summary>
    /// Unity Editor menu items for LensLab. Accessible via the top menu bar under
    /// <b>LensLab/</b>.
    ///
    /// <b>LensLab → Setup → Create Live AR Scene</b>
    /// Creates the complete GameObject hierarchy required for a live AR session and
    /// wires up all component references automatically. Run this once in a new scene,
    /// then press Play.
    ///
    /// Created hierarchy
    /// -----------------
    /// <code>
    /// LensLabBootstrap          — CalibrationLoader, PoseLoader, PoseServerLauncher
    /// Main Camera               — Camera, CameraProjectionController, ProjectionValidationOverlay
    /// LensLabLiveCamera         — WebCamSource, LiveCameraBackground, PoseClient, LivePoseReceiver
    /// LensLabARContent          — LivePoseReceiver target; matchBoardScale = false
    ///   BoardOutline            — Quad, Sprites/Default semi-transparent cyan
    ///   PoseAxes                — empty parent
    ///     AxisX                 — Cube, red
    ///     AxisY                 — Cube, green
    ///     AxisZ                 — Cube, blue
    /// LensLabHUD (Canvas)       — Screen Space Overlay
    ///   Panel                   — semi-transparent background
    ///     ServerStatus (Text)
    ///     BoardStatus  (Text)
    ///     Metrics      (Text)
    ///   LensLabStatusHUD        — component on Canvas root
    /// </code>
    /// </summary>
    public static class LensLabEditorMenu
    {
        // ------------------------------------------------------------------
        // Menu items
        // ------------------------------------------------------------------

        [MenuItem("LensLab/Setup/Create Live AR Scene", priority = 1)]
        private static void CreateLiveArScene()
        {
            Undo.SetCurrentGroupName("LensLab: Create Live AR Scene");
            var group = Undo.GetCurrentGroup();

            var bootstrap  = GetOrCreateBootstrap();
            var mainCamera = GetOrConfigureMainCamera();
            var liveCamera = CreateLiveCameraObject();
            var arContent  = CreateArContent();
            var hud        = CreateStatusHud();

            WireReferences(bootstrap, mainCamera, liveCamera, arContent, hud);

            Undo.CollapseUndoOperations(group);

            Selection.activeGameObject = bootstrap;
            EditorGUIUtility.PingObject(bootstrap);

            Debug.Log(
                "[LensLab] Live AR scene created.\n" +
                "Press Play to start. The Python pose server will launch automatically " +
                "if LensLabPoseServerLauncher is configured correctly.\n" +
                "Adjust LensLabLivePoseReceiver → Board Local Offset / Board Size Meters " +
                "to match your ChArUco board dimensions."
            );
        }

        [MenuItem("LensLab/Setup/Create Live AR Scene", validate = true)]
        private static bool ValidateCreateLiveArScene() => true;

        // ------------------------------------------------------------------
        // Object creation helpers
        // ------------------------------------------------------------------

        private static GameObject GetOrCreateBootstrap()
        {
            var existing = GameObject.Find("LensLabBootstrap");
            if (existing != null)
            {
                EnsureComponent<LensLabCalibrationLoader>(existing);
                EnsureComponent<LensLabPoseLoader>(existing);
                EnsureComponent<LensLabPoseServerLauncher>(existing);
                return existing;
            }

            var go = new GameObject("LensLabBootstrap");
            Undo.RegisterCreatedObjectUndo(go, "Create LensLabBootstrap");
            go.AddComponent<LensLabCalibrationLoader>();
            go.AddComponent<LensLabPoseLoader>();
            go.AddComponent<LensLabPoseServerLauncher>();
            return go;
        }

        private static GameObject GetOrConfigureMainCamera()
        {
            var cam = Camera.main;
            GameObject go;

            if (cam != null)
            {
                go = cam.gameObject;
            }
            else
            {
                go = new GameObject("Main Camera");
                Undo.RegisterCreatedObjectUndo(go, "Create Main Camera");
                go.tag = "MainCamera";
                go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            EnsureComponent<LensLabCameraProjectionController>(go);
            EnsureComponent<LensLabProjectionValidationOverlay>(go);
            return go;
        }

        private static GameObject CreateLiveCameraObject()
        {
            const string name = "LensLabLiveCamera";
            var go = GameObject.Find(name) ?? CreateGO(name);

            EnsureComponent<LensLabWebCamSource>(go);
            EnsureComponent<LensLabLiveCameraBackground>(go);
            EnsureComponent<LensLabPoseClient>(go);
            EnsureComponent<LensLabLivePoseReceiver>(go);
            return go;
        }

        /// <summary>
        /// Creates the LensLabARContent hierarchy:
        ///   LensLabARContent  (root, empty — driven by LivePoseReceiver)
        ///     BoardOutline    (Quad, Sprites/Default, semi-transparent cyan)
        ///     PoseAxes        (empty)
        ///       AxisX         (Cube, red,   elongated along X)
        ///       AxisY         (Cube, green, elongated along Y)
        ///       AxisZ         (Cube, blue,  elongated along Z)
        /// </summary>
        private static GameObject CreateArContent()
        {
            const string rootName = "LensLabARContent";
            var root = GameObject.Find(rootName);
            if (root == null)
            {
                root = CreateGO(rootName);
            }

            root.transform.position = new Vector3(0f, 0f, -8f);
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            // ----- BoardOutline -----
            var outline = FindOrCreateChild(root, "BoardOutline", () =>
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                q.name = "BoardOutline";
                Object.DestroyImmediate(q.GetComponent<Collider>());
                return q;
            });

            outline.transform.SetParent(root.transform, false);
            // Scale matches default board: 7×5 squares × 0.025 m = 0.175 × 0.125 m
            outline.transform.localScale    = new Vector3(0.175f, 0.125f, 0.001f);
            outline.transform.localPosition = Vector3.zero;
            outline.transform.localRotation = Quaternion.identity;

            var outlineRenderer = outline.GetComponent<MeshRenderer>();
            if (outlineRenderer != null)
            {
                var mat = new Material(Shader.Find("Sprites/Default"))
                {
                    color = new Color(0.2f, 0.85f, 1f, 0.30f),
                };
                outlineRenderer.sharedMaterial = mat;
            }

            // ----- PoseAxes -----
            var axesParent = FindOrCreateChild(root, "PoseAxes", () => new GameObject("PoseAxes"));
            axesParent.transform.SetParent(root.transform, false);
            axesParent.transform.localPosition = Vector3.zero;
            axesParent.transform.localRotation = Quaternion.identity;
            axesParent.transform.localScale    = Vector3.one;

            CreateAxisCube(axesParent, "AxisX",
                new Color(1f, 0.27f, 0.27f),            // #FF4444
                new Vector3(0.06f, 0.004f, 0.004f),
                new Vector3(0.03f, 0f, 0f));

            CreateAxisCube(axesParent, "AxisY",
                new Color(0.27f, 1f, 0.27f),            // #44FF44
                new Vector3(0.004f, 0.06f, 0.004f),
                new Vector3(0f, 0.03f, 0f));

            CreateAxisCube(axesParent, "AxisZ",
                new Color(0.27f, 0.40f, 1f),            // #4466FF
                new Vector3(0.004f, 0.004f, 0.06f),
                new Vector3(0f, 0f, 0.03f));

            root.SetActive(false);   // LivePoseReceiver activates it when board is detected
            return root;
        }

        private static void CreateAxisCube(
            GameObject parent,
            string cubeChildName,
            Color color,
            Vector3 localScale,
            Vector3 localPosition)
        {
            var existing = parent.transform.Find(cubeChildName);
            GameObject cube;

            if (existing != null)
            {
                cube = existing.gameObject;
            }
            else
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = cubeChildName;
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                Undo.RegisterCreatedObjectUndo(cube, $"Create {cubeChildName}");
                cube.transform.SetParent(parent.transform, false);
            }

            cube.transform.localScale    = localScale;
            cube.transform.localPosition = localPosition;
            cube.transform.localRotation = Quaternion.identity;

            var renderer = cube.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard")) { color = color };
                renderer.sharedMaterial = mat;
            }
        }

        /// <summary>
        /// Creates the Screen Space HUD Canvas with a semi-transparent panel and three
        /// Text labels for server status, board status, and reprojection metrics.
        /// </summary>
        private static GameObject CreateStatusHud()
        {
            const string canvasName = "LensLabHUD";
            var existing = GameObject.Find(canvasName);
            if (existing != null)
            {
                EnsureComponent<LensLabStatusHUD>(existing);
                return existing;
            }

            // ---- Canvas ----
            var canvasGo = CreateGO(canvasName);
            var canvas   = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // ---- Panel ----
            var panelGo    = CreateChildGO(canvasGo, "Panel");
            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.55f);

            var panelRect = panelGo.GetComponent<RectTransform>();
            // Anchor to top-left; 180 × 90 px panel
            panelRect.anchorMin        = new Vector2(0f, 1f);
            panelRect.anchorMax        = new Vector2(0f, 1f);
            panelRect.pivot            = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(12f, -12f);
            panelRect.sizeDelta        = new Vector2(220f, 88f);

            // ---- Header label ----
            var headerText = CreateTextLabel(panelGo, "Header",
                "<color=#FFFFFF><b>[ LensLab ]</b></color>",
                new Vector2(8f, -6f), new Vector2(204f, 20f));
            headerText.fontSize  = 13;
            headerText.alignment = TextAnchor.MiddleLeft;

            // ---- Status text rows ----
            var serverText = CreateTextLabel(panelGo, "ServerStatus", "",
                new Vector2(8f, -28f), new Vector2(204f, 18f));
            var boardText = CreateTextLabel(panelGo, "BoardStatus", "",
                new Vector2(8f, -48f), new Vector2(204f, 18f));
            var metricsText = CreateTextLabel(panelGo, "Metrics", "",
                new Vector2(8f, -68f), new Vector2(204f, 18f));

            // ---- LensLabStatusHUD component ----
            var hud = canvasGo.AddComponent<LensLabStatusHUD>();
            var so  = new SerializedObject(hud);
            SetProperty(so, "serverStatusText", serverText);
            SetProperty(so, "boardStatusText",  boardText);
            SetProperty(so, "metricsText",       metricsText);
            so.ApplyModifiedProperties();

            return canvasGo;
        }

        // ------------------------------------------------------------------
        // Reference wiring
        // ------------------------------------------------------------------

        private static void WireReferences(
            GameObject bootstrap,
            GameObject mainCamera,
            GameObject liveCamera,
            GameObject arContent,
            GameObject hud)
        {
            var calibLoader    = bootstrap.GetComponent<LensLabCalibrationLoader>();
            var poseLoader     = bootstrap.GetComponent<LensLabPoseLoader>();
            var camComp        = mainCamera.GetComponent<Camera>();
            var projCtrl       = mainCamera.GetComponent<LensLabCameraProjectionController>();
            var valOverlay     = mainCamera.GetComponent<LensLabProjectionValidationOverlay>();
            var webCamSource   = liveCamera.GetComponent<LensLabWebCamSource>();
            var liveBackground = liveCamera.GetComponent<LensLabLiveCameraBackground>();
            var poseClient     = liveCamera.GetComponent<LensLabPoseClient>();
            var poseReceiver   = liveCamera.GetComponent<LensLabLivePoseReceiver>();

            // LensLabCameraProjectionController
            SetProperty(projCtrl,      "calibrationLoader", calibLoader);
            SetProperty(projCtrl,      "targetCamera",      camComp);

            // LensLabProjectionValidationOverlay
            SetProperty(valOverlay,    "calibrationLoader", calibLoader);
            SetProperty(valOverlay,    "targetCamera",      camComp);

            // LensLabLiveCameraBackground
            SetProperty(liveBackground, "webCamSource",      webCamSource);
            SetProperty(liveBackground, "validationOverlay", valOverlay);
            SetProperty(liveBackground, "calibrationLoader", calibLoader);
            SetBoolProperty(liveBackground, "useCanvasBackgroundForRawLiveTest", false);

            // LensLabLivePoseReceiver
            SetProperty(poseReceiver, "poseTarget",      arContent.transform);
            SetProperty(poseReceiver, "targetCamera",    camComp);
            SetBoolProperty(poseReceiver, "matchBoardScale", false);

            // LensLabStatusHUD — auto-find is fine but wire explicitly for robustness
            var hudComp = hud.GetComponent<LensLabStatusHUD>();
            if (hudComp != null)
            {
                SetProperty(hudComp, "poseClient", poseClient);
            }
        }

        // ------------------------------------------------------------------
        // Low-level helpers
        // ------------------------------------------------------------------

        private static GameObject CreateGO(string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            return go;
        }

        private static GameObject CreateChildGO(GameObject parent, string name)
        {
            var go = CreateGO(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        /// <summary>
        /// Find an existing direct child by name, or create it using the supplied factory.
        /// Registers the created object with Undo.
        /// </summary>
        private static GameObject FindOrCreateChild(GameObject parent, string childName, System.Func<GameObject> factory)
        {
            var existing = parent.transform.Find(childName);
            if (existing != null)
            {
                return existing.gameObject;
            }

            var go = factory();
            go.name = childName;
            Undo.RegisterCreatedObjectUndo(go, $"Create {childName}");
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static Text CreateTextLabel(
            GameObject parent,
            string objName,
            string initialText,
            Vector2 anchoredPos,
            Vector2 sizeDelta)
        {
            var go   = CreateChildGO(parent, objName);
            var text = go.AddComponent<Text>();

            text.text      = initialText;
            // Try the Unity 2022+ built-in font name, fall back to the legacy name.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                     ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize  = 12;
            text.color     = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = true;

            var rt          = go.GetComponent<RectTransform>();
            rt.anchorMin    = new Vector2(0f, 1f);
            rt.anchorMax    = new Vector2(0f, 1f);
            rt.pivot        = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta    = sizeDelta;

            return text;
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        private static void SetProperty(Object target, string propertyName, Object value)
        {
            if (target == null) return;
            var so   = new SerializedObject(target);
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[LensLab] SetProperty: '{propertyName}' not found on {target.GetType().Name}.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedProperties();
        }

        private static void SetProperty(SerializedObject so, string propertyName, Object value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[LensLab] SetProperty: '{propertyName}' not found on {so.targetObject.GetType().Name}.");
                return;
            }
            prop.objectReferenceValue = value;
        }

        private static void SetBoolProperty(Object target, string propertyName, bool value)
        {
            if (target == null) return;
            var so   = new SerializedObject(target);
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[LensLab] SetBoolProperty: '{propertyName}' not found on {target.GetType().Name}.");
                return;
            }
            prop.boolValue = value;
            so.ApplyModifiedProperties();
        }
    }
}
