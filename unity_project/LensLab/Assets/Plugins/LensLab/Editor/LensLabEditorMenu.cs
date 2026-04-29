using LensLab.Runtime;
using UnityEditor;
using UnityEngine;

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

            var bootstrap   = GetOrCreateBootstrap();
            var mainCamera  = GetOrConfigureMainCamera();
            var liveCamera  = CreateLiveCameraObject();
            var arContent   = CreateArContentQuad();

            WireReferences(bootstrap, mainCamera, liveCamera, arContent);

            Undo.CollapseUndoOperations(group);

            // Select the hierarchy root for a clear overview.
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
        private static bool ValidateCreateLiveArScene()
        {
            // Allow in any open scene.
            return true;
        }

        // ------------------------------------------------------------------
        // Object creation helpers
        // ------------------------------------------------------------------

        /// <summary>Find or create the LensLabBootstrap root with all loader components.</summary>
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

        /// <summary>
        /// Find the scene's Main Camera (or create one) and add the projection +
        /// validation overlay components.
        /// </summary>
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

        /// <summary>
        /// Create the LensLabLiveCamera object that drives the camera feed, client,
        /// and receiver. If one already exists its components are refreshed.
        /// </summary>
        private static GameObject CreateLiveCameraObject()
        {
            const string name = "LensLabLiveCamera";
            var existing = GameObject.Find(name);
            GameObject go;

            if (existing != null)
            {
                go = existing;
            }
            else
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create LensLabLiveCamera");
            }

            EnsureComponent<LensLabWebCamSource>(go);
            EnsureComponent<LensLabLiveCameraBackground>(go);
            EnsureComponent<LensLabPoseClient>(go);
            EnsureComponent<LensLabLivePoseReceiver>(go);
            return go;
        }

        /// <summary>
        /// Create (or find) the LensLabARContent Quad that serves as the virtual
        /// content anchor driven by the live pose.
        /// </summary>
        private static GameObject CreateArContentQuad()
        {
            const string name = "LensLabARContent";
            var existing = GameObject.Find(name);
            if (existing != null)
            {
                return existing;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create LensLabARContent");

            // Remove the default collider — not needed for AR overlay.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            // Assign a simple semi-transparent unlit material so the overlay is
            // visible immediately without any extra material setup.
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Unlit/Color"))
                {
                    color = new Color(0.2f, 0.8f, 1f, 0.35f),
                };
                renderer.sharedMaterial = mat;
            }

            // Start inactive; LensLabLivePoseReceiver will activate it when a board
            // is detected.
            go.SetActive(false);
            return go;
        }

        // ------------------------------------------------------------------
        // Reference wiring
        // ------------------------------------------------------------------

        private static void WireReferences(
            GameObject bootstrap,
            GameObject mainCamera,
            GameObject liveCamera,
            GameObject arContent)
        {
            var calibLoader   = bootstrap.GetComponent<LensLabCalibrationLoader>();
            var poseLoader    = bootstrap.GetComponent<LensLabPoseLoader>();
            var camComp       = mainCamera.GetComponent<Camera>();
            var projCtrl      = mainCamera.GetComponent<LensLabCameraProjectionController>();
            var valOverlay    = mainCamera.GetComponent<LensLabProjectionValidationOverlay>();
            var webCamSource  = liveCamera.GetComponent<LensLabWebCamSource>();
            var liveBackground = liveCamera.GetComponent<LensLabLiveCameraBackground>();
            var poseReceiver  = liveCamera.GetComponent<LensLabLivePoseReceiver>();

            // LensLabCameraProjectionController
            SetProperty(projCtrl, "calibrationLoader", calibLoader);
            SetProperty(projCtrl, "targetCamera",      camComp);

            // LensLabProjectionValidationOverlay
            SetProperty(valOverlay, "calibrationLoader", calibLoader);
            SetProperty(valOverlay, "targetCamera",      camComp);

            // LensLabLiveCameraBackground
            SetProperty(liveBackground, "webCamSource",      webCamSource);
            SetProperty(liveBackground, "validationOverlay", valOverlay);
            SetProperty(liveBackground, "calibrationLoader", calibLoader);

            // LensLabLivePoseReceiver: point poseTarget at the AR content quad.
            SetProperty(poseReceiver, "poseTarget", arContent.transform);
        }

        // ------------------------------------------------------------------
        // Utility
        // ------------------------------------------------------------------

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            if (existing != null)
            {
                return existing;
            }

            Undo.RecordObject(go, $"Add {typeof(T).Name}");
            return go.AddComponent<T>();
        }

        /// <summary>
        /// Sets a serialized field by name using <see cref="SerializedObject"/> so the
        /// change is recorded for Undo and correctly dirtied.
        /// </summary>
        private static void SetProperty(Object target, string propertyName, Object value)
        {
            if (target == null)
            {
                return;
            }

            var so   = new SerializedObject(target);
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning(
                    $"[LensLab] SetProperty: property '{propertyName}' not found on {target.GetType().Name}."
                );
                return;
            }

            prop.objectReferenceValue = value;
            so.ApplyModifiedProperties();
        }
    }
}
