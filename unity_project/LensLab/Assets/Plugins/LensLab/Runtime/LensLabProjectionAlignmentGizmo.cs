using UnityEngine;

namespace LensLab.Runtime
{
    [ExecuteAlways]
    public class LensLabProjectionAlignmentGizmo : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private Camera targetCamera;

        [Header("Placement")]
        [SerializeField] private bool attachToCamera = true;
        [SerializeField] private float distance = 2.0f;
        [SerializeField] private Vector3 localOffset = Vector3.zero;
        [SerializeField] private Vector3 localEulerAngles = Vector3.zero;

        [Header("Frame")]
        [SerializeField] private Vector2 frameSize = new Vector2(1.0f, 1.8f);
        [SerializeField] private bool showInnerFrame = true;
        [SerializeField] [Range(0.1f, 0.95f)] private float innerFrameScale = 0.72f;
        [SerializeField] private bool showCenterCross = true;
        [SerializeField] private float centerCrossSize = 0.12f;
        [SerializeField] private float lineWidth = 0.01f;

        [Header("Visual")]
        [SerializeField] private Color outerFrameColor = new Color(0.15f, 0.95f, 1f, 1f);
        [SerializeField] private Color innerFrameColor = new Color(1f, 0.72f, 0.2f, 1f);
        [SerializeField] private Color centerCrossColor = new Color(1f, 0.35f, 0.15f, 1f);
        [SerializeField] private bool xRay = true;
        [SerializeField] private int sortingOrder = 50;

        [Header("Behavior")]
        [SerializeField] private bool buildOnEnable = true;
        [SerializeField] private bool updateEveryFrame = true;
        [SerializeField] private bool verboseLogging = true;

        private const string RuntimeRootName = "LensLabProjectionAlignmentGizmo_Runtime";

        private Transform runtimeRoot;
        private LineRenderer outerFrameRenderer;
        private LineRenderer innerFrameRenderer;
        private LineRenderer centerHorizontalRenderer;
        private LineRenderer centerVerticalRenderer;
        private Material lineMaterial;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        private Vector2 lastFrameSize;

        private void Reset()
        {
            TryAutoAssignCamera();
        }

        private void OnEnable()
        {
            TryAutoAssignCamera();
            if (buildOnEnable)
            {
                RefreshGizmo();
            }
        }

        private void LateUpdate()
        {
            if (!updateEveryFrame)
            {
                return;
            }

            RefreshGizmo();
        }

        [ContextMenu("Refresh Gizmo")]
        public void RefreshGizmo()
        {
            TryAutoAssignCamera();
            if (targetCamera == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabProjectionAlignmentGizmo)}] Missing target camera. " +
                    "Add this component to a Camera or assign one explicitly.",
                    this
                );
                return;
            }

            EnsureRuntimeHierarchy();
            UpdateTransform();
            UpdateLines();

            var shouldLog = verboseLogging
                && (runtimeRoot.position != lastPosition || runtimeRoot.rotation != lastRotation || frameSize != lastFrameSize);

            lastPosition = runtimeRoot.position;
            lastRotation = runtimeRoot.rotation;
            lastFrameSize = frameSize;

            if (shouldLog)
            {
                Debug.Log(BuildDebugSummary(), this);
            }
        }

        private void TryAutoAssignCamera()
        {
            if (targetCamera == null)
            {
                targetCamera = GetComponent<Camera>();
            }
        }

        private void EnsureRuntimeHierarchy()
        {
            if (runtimeRoot != null && outerFrameRenderer != null)
            {
                return;
            }

            var existing = transform.Find(RuntimeRootName);
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
            }

            var rootObject = new GameObject(RuntimeRootName);
            rootObject.transform.SetParent(attachToCamera && targetCamera != null ? targetCamera.transform : transform, false);
            runtimeRoot = rootObject.transform;

            lineMaterial = new Material(Shader.Find("Sprites/Default"));
            if (xRay)
            {
                lineMaterial.renderQueue = 4000;
            }

            outerFrameRenderer = CreateLineRenderer("OuterFrame", outerFrameColor);
            innerFrameRenderer = CreateLineRenderer("InnerFrame", innerFrameColor);
            centerHorizontalRenderer = CreateLineRenderer("CenterHorizontal", centerCrossColor);
            centerVerticalRenderer = CreateLineRenderer("CenterVertical", centerCrossColor);
        }

        private LineRenderer CreateLineRenderer(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(runtimeRoot, false);

            var renderer = go.AddComponent<LineRenderer>();
            renderer.sharedMaterial = lineMaterial;
            renderer.widthMultiplier = lineWidth;
            renderer.positionCount = 0;
            renderer.loop = false;
            renderer.useWorldSpace = false;
            renderer.numCapVertices = 4;
            renderer.numCornerVertices = 4;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.textureMode = LineTextureMode.Stretch;
            renderer.alignment = LineAlignment.TransformZ;
            renderer.sortingOrder = sortingOrder;
            renderer.startColor = color;
            renderer.endColor = color;
            return renderer;
        }

        private void UpdateTransform()
        {
            runtimeRoot.localPosition = new Vector3(localOffset.x, localOffset.y, distance + localOffset.z);
            runtimeRoot.localRotation = Quaternion.Euler(localEulerAngles);
            runtimeRoot.localScale = Vector3.one;
        }

        private void UpdateLines()
        {
            DrawRectangle(outerFrameRenderer, frameSize, true);

            innerFrameRenderer.enabled = showInnerFrame;
            if (showInnerFrame)
            {
                DrawRectangle(innerFrameRenderer, frameSize * innerFrameScale, true);
            }

            centerHorizontalRenderer.enabled = showCenterCross;
            centerVerticalRenderer.enabled = showCenterCross;
            if (showCenterCross)
            {
                var halfCross = centerCrossSize * 0.5f;
                DrawSegment(centerHorizontalRenderer, new Vector3(-halfCross, 0f, 0f), new Vector3(halfCross, 0f, 0f));
                DrawSegment(centerVerticalRenderer, new Vector3(0f, -halfCross, 0f), new Vector3(0f, halfCross, 0f));
            }
        }

        private static void DrawRectangle(LineRenderer renderer, Vector2 size, bool closed)
        {
            var halfWidth = size.x * 0.5f;
            var halfHeight = size.y * 0.5f;
            renderer.positionCount = closed ? 5 : 4;
            renderer.SetPosition(0, new Vector3(-halfWidth, -halfHeight, 0f));
            renderer.SetPosition(1, new Vector3(-halfWidth, halfHeight, 0f));
            renderer.SetPosition(2, new Vector3(halfWidth, halfHeight, 0f));
            renderer.SetPosition(3, new Vector3(halfWidth, -halfHeight, 0f));
            if (closed)
            {
                renderer.SetPosition(4, new Vector3(-halfWidth, -halfHeight, 0f));
            }
        }

        private static void DrawSegment(LineRenderer renderer, Vector3 start, Vector3 end)
        {
            renderer.positionCount = 2;
            renderer.SetPosition(0, start);
            renderer.SetPosition(1, end);
        }

        private string BuildDebugSummary()
        {
            return
                $"[{nameof(LensLabProjectionAlignmentGizmo)}] Refreshed alignment gizmo.\n" +
                $"Distance: {distance:F3}m\n" +
                $"Frame Size: {frameSize.x:F3} x {frameSize.y:F3}\n" +
                $"Local Offset: {localOffset}\n" +
                $"Local Rotation: {localEulerAngles}";
        }

        private void OnDestroy()
        {
            if (lineMaterial != null)
            {
                DestroyImmediate(lineMaterial);
                lineMaterial = null;
            }
        }
    }
}
