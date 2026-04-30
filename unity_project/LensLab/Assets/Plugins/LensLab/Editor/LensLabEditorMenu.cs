using LensLab.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Editor
{
    /// <summary>
    /// Creates the runtime demo scene for the live TCP AR workflow.
    /// </summary>
    public static class LensLabEditorMenu
    {
        [MenuItem("LensLab/Setup/Create Live AR Scene", priority = 1)]
        private static void CreateLiveArScene()
        {
            Undo.SetCurrentGroupName("LensLab: Create Live AR Scene");
            var group = Undo.GetCurrentGroup();

            var bootstrap = GetOrCreateBootstrap();
            var mainCamera = GetOrConfigureMainCamera();
            var liveCamera = GetOrCreateLiveCameraObject();
            var arContent = GetOrCreateArContent();
            var hud = GetOrCreateStatusHud();

            WireReferences(bootstrap, mainCamera, liveCamera, arContent, hud);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = bootstrap;
            EditorGUIUtility.PingObject(bootstrap);

            Debug.Log(
                "[LensLab] Live AR scene created.\n" +
                "Press Play to start the Python pose server, receive TCP camera frames, " +
                "and anchor LensLabARContent from the live ChArUco pose."
            );
        }

        [MenuItem("LensLab/Setup/Create Live AR Scene", validate = true)]
        private static bool ValidateCreateLiveArScene() => true;

        private static GameObject GetOrCreateBootstrap()
        {
            var go = GameObject.Find("LensLabBootstrap") ?? CreateGO("LensLabBootstrap");
            EnsureComponent<LensLabCalibrationLoader>(go);
            EnsureComponent<LensLabPoseServerLauncher>(go);
            return go;
        }

        private static GameObject GetOrConfigureMainCamera()
        {
            GameObject go;
            var existing = Camera.main;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = CreateGO("Main Camera");
                go.tag = "MainCamera";
                go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            EnsureComponent<LensLabCameraProjectionController>(go);
            EnsureComponent<LensLabProjectionValidationOverlay>(go);
            return go;
        }

        private static GameObject GetOrCreateLiveCameraObject()
        {
            var go = GameObject.Find("LensLabLiveCamera") ?? CreateGO("LensLabLiveCamera");
            EnsureComponent<LensLabPoseClient>(go);
            EnsureComponent<LensLabLiveCameraBackground>(go);
            EnsureComponent<LensLabLivePoseReceiver>(go);
            return go;
        }

        private static GameObject GetOrCreateArContent()
        {
            var root = GameObject.Find("LensLabARContent") ?? CreateGO("LensLabARContent");
            root.transform.position = new Vector3(0f, 0f, -8f);
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var outline = FindOrCreateChild(root, "BoardOutline", () =>
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "BoardOutline";
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                return quad;
            });
            outline.transform.SetParent(root.transform, false);
            outline.transform.localScale = new Vector3(0.175f, 0.125f, 0.001f);
            outline.transform.localPosition = Vector3.zero;
            outline.transform.localRotation = Quaternion.identity;
            SetMaterial(outline, Shader.Find("Sprites/Default"), new Color(0.2f, 0.85f, 1f, 0.30f));

            var axesParent = FindOrCreateChild(root, "PoseAxes", () => new GameObject("PoseAxes"));
            axesParent.transform.SetParent(root.transform, false);
            axesParent.transform.localPosition = Vector3.zero;
            axesParent.transform.localRotation = Quaternion.identity;
            axesParent.transform.localScale = Vector3.one;

            CreateAxisCube(axesParent, "AxisX", new Color(1f, 0.27f, 0.27f),
                new Vector3(0.06f, 0.004f, 0.004f), new Vector3(0.03f, 0f, 0f));
            CreateAxisCube(axesParent, "AxisY", new Color(0.27f, 1f, 0.27f),
                new Vector3(0.004f, 0.06f, 0.004f), new Vector3(0f, 0.03f, 0f));
            CreateAxisCube(axesParent, "AxisZ", new Color(0.27f, 0.40f, 1f),
                new Vector3(0.004f, 0.004f, 0.06f), new Vector3(0f, 0f, 0.03f));

            root.SetActive(false);
            return root;
        }

        private static GameObject GetOrCreateStatusHud()
        {
            var existing = GameObject.Find("LensLabHUD");
            if (existing != null)
            {
                EnsureComponent<LensLabStatusHUD>(existing);
                return existing;
            }

            var canvasGo = CreateGO("LensLabHUD");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            var panelGo = CreateChildGO(canvasGo, "Panel");
            var panelImage = panelGo.AddComponent<Image>();
            panelImage.color = new Color(0f, 0f, 0f, 0.55f);

            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(12f, -12f);
            panelRect.sizeDelta = new Vector2(220f, 88f);

            var headerText = CreateTextLabel(panelGo, "Header",
                "<color=#FFFFFF><b>[ LensLab ]</b></color>",
                new Vector2(8f, -6f), new Vector2(204f, 20f));
            headerText.fontSize = 13;

            var serverText = CreateTextLabel(panelGo, "ServerStatus", "",
                new Vector2(8f, -28f), new Vector2(204f, 18f));
            var boardText = CreateTextLabel(panelGo, "BoardStatus", "",
                new Vector2(8f, -48f), new Vector2(204f, 18f));
            var metricsText = CreateTextLabel(panelGo, "Metrics", "",
                new Vector2(8f, -68f), new Vector2(204f, 18f));

            var hud = canvasGo.AddComponent<LensLabStatusHUD>();
            var so = new SerializedObject(hud);
            SetProperty(so, "serverStatusText", serverText);
            SetProperty(so, "boardStatusText", boardText);
            SetProperty(so, "metricsText", metricsText);
            so.ApplyModifiedProperties();

            return canvasGo;
        }

        private static void WireReferences(
            GameObject bootstrap,
            GameObject mainCamera,
            GameObject liveCamera,
            GameObject arContent,
            GameObject hud)
        {
            var calibLoader = bootstrap.GetComponent<LensLabCalibrationLoader>();
            var camComp = mainCamera.GetComponent<Camera>();
            var projCtrl = mainCamera.GetComponent<LensLabCameraProjectionController>();
            var valOverlay = mainCamera.GetComponent<LensLabProjectionValidationOverlay>();
            var liveBackground = liveCamera.GetComponent<LensLabLiveCameraBackground>();
            var poseClient = liveCamera.GetComponent<LensLabPoseClient>();
            var poseReceiver = liveCamera.GetComponent<LensLabLivePoseReceiver>();

            SetProperty(projCtrl, "calibrationLoader", calibLoader);
            SetProperty(projCtrl, "targetCamera", camComp);

            SetProperty(valOverlay, "calibrationLoader", calibLoader);
            SetProperty(valOverlay, "targetCamera", camComp);
            SetProperty(valOverlay, "backgroundTexture", null);
            SetBoolProperty(valOverlay, "loadBackgroundFromResourcesIfMissing", false);

            SetProperty(liveBackground, "poseClient", poseClient);
            SetProperty(liveBackground, "validationOverlay", valOverlay);
            SetProperty(liveBackground, "calibrationLoader", calibLoader);
            SetBoolProperty(liveBackground, "useCanvasBackgroundForRawLiveTest", false);
            SetBoolProperty(liveBackground, "useGpuUndistortion", false);

            SetProperty(poseReceiver, "poseTarget", arContent.transform);
            SetProperty(poseReceiver, "targetCamera", camComp);
            SetBoolProperty(poseReceiver, "matchBoardScale", false);

            var hudComp = hud.GetComponent<LensLabStatusHUD>();
            if (hudComp != null)
            {
                SetProperty(hudComp, "poseClient", poseClient);
            }
        }

        private static void CreateAxisCube(
            GameObject parent,
            string name,
            Color color,
            Vector3 localScale,
            Vector3 localPosition)
        {
            var existing = parent.transform.Find(name);
            var cube = existing != null ? existing.gameObject : GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            var collider = cube.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }
            if (existing == null)
            {
                Undo.RegisterCreatedObjectUndo(cube, $"Create {name}");
            }
            cube.transform.SetParent(parent.transform, false);
            cube.transform.localScale = localScale;
            cube.transform.localPosition = localPosition;
            cube.transform.localRotation = Quaternion.identity;
            SetMaterial(cube, Shader.Find("Standard"), color);
        }

        private static void SetMaterial(GameObject go, Shader shader, Color color)
        {
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null || shader == null)
            {
                return;
            }

            renderer.sharedMaterial = new Material(shader) { color = color };
        }

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

        private static GameObject FindOrCreateChild(
            GameObject parent,
            string childName,
            System.Func<GameObject> factory)
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
            string name,
            string initialText,
            Vector2 anchoredPos,
            Vector2 sizeDelta)
        {
            var go = CreateChildGO(parent, name);
            var text = go.AddComponent<Text>();
            text.text = initialText;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 12;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = true;

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = sizeDelta;

            return text;
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        private static void SetProperty(Object target, string propertyName, Object value)
        {
            if (target == null)
            {
                return;
            }

            var so = new SerializedObject(target);
            SetProperty(so, propertyName, value);
            so.ApplyModifiedProperties();
        }

        private static void SetProperty(SerializedObject so, string propertyName, Object value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[LensLab] Property '{propertyName}' not found on {so.targetObject.GetType().Name}.");
                return;
            }

            prop.objectReferenceValue = value;
        }

        private static void SetBoolProperty(Object target, string propertyName, bool value)
        {
            if (target == null)
            {
                return;
            }

            var so = new SerializedObject(target);
            var prop = so.FindProperty(propertyName);
            if (prop == null)
            {
                Debug.LogWarning($"[LensLab] Property '{propertyName}' not found on {target.GetType().Name}.");
                return;
            }

            prop.boolValue = value;
            so.ApplyModifiedProperties();
        }
    }
}
