using UnityEngine;

namespace LensLab.Runtime
{
    [ExecuteAlways]
    public class LensLabPoseBoardDebug : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabPoseLoader poseLoader;
        [SerializeField] private LensLabProjectionValidationOverlay validationOverlay;
        [SerializeField] private Camera targetCamera;

        [Header("Board Debug")]
        [SerializeField] private bool createOnEnable = true;
        [SerializeField] private bool rebuildEveryFrame = true;
        [SerializeField] private bool verboseLogging = true;
        [SerializeField] private float boardOpacity = 0.24f;
        [SerializeField] private Color boardTint = new Color(0.1f, 0.9f, 1f, 0.24f);
        [SerializeField] private float renderDistanceOffset = 0.01f;

        private const string RuntimeRootName = "LensLabPoseBoardDebug_Runtime";
        private const HideFlags RuntimeHideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

        private Transform runtimeRoot;
        private MeshRenderer boardRenderer;
        private MeshFilter boardMeshFilter;
        private Material boardMaterial;
        private Mesh boardMesh;
        private Vector3[] lastWorldCorners;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void OnEnable()
        {
            TryAutoAssignDependencies();
            if (createOnEnable)
            {
                RefreshBoard();
            }
        }

        private void LateUpdate()
        {
            if (!rebuildEveryFrame)
            {
                return;
            }

            RefreshBoard();
        }

        [ContextMenu("Refresh Board Debug")]
        public void RefreshBoard()
        {
            if (!ValidateSetup(out var poseData, out var imageCorners))
            {
                return;
            }

            HideValidationGuideOverlay();
            EnsureRuntimeObjects();
            UpdateBoardGeometry(poseData, imageCorners);

            if (verboseLogging)
            {
                var worldCorners = BuildWorldCorners(poseData, imageCorners);
                if (!CornersApproximatelyEqual(worldCorners, lastWorldCorners))
                {
                    Debug.Log(BuildDebugSummary(worldCorners), this);
                    lastWorldCorners = worldCorners;
                }
            }
        }

        private void TryAutoAssignDependencies()
        {
            if (poseLoader == null)
            {
                poseLoader = GetComponent<LensLabPoseLoader>();
            }

            if (validationOverlay == null)
            {
                validationOverlay = GetComponent<LensLabProjectionValidationOverlay>();
            }

            if (targetCamera == null)
            {
                targetCamera = GetComponent<Camera>();
            }

            if (targetCamera == null && validationOverlay != null)
            {
                targetCamera = validationOverlay.TargetCamera;
            }
        }

        private bool ValidateSetup(out LensLabPoseData poseData, out Vector2[] imageCorners)
        {
            poseData = null;
            imageCorners = null;

            TryAutoAssignDependencies();

            if (poseLoader == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabPoseBoardDebug)}] Missing pose loader. " +
                    "Assign one explicitly or add LensLabPoseLoader to the same GameObject.",
                    this
                );
                return false;
            }

            if (targetCamera == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabPoseBoardDebug)}] Missing target camera. " +
                    "Assign one explicitly or pair this component with LensLabProjectionValidationOverlay.",
                    this
                );
                return false;
            }

            if (!poseLoader.HasValidPose)
            {
                poseLoader.LoadPose();
                if (!poseLoader.HasValidPose)
                {
                    Debug.LogError($"[{nameof(LensLabPoseBoardDebug)}] Pose data is not available.", this);
                    return false;
                }
            }

            poseData = poseLoader.LoadedPose;
            if (poseData == null || !poseData.IsValid())
            {
                Debug.LogError($"[{nameof(LensLabPoseBoardDebug)}] Pose data is invalid.", this);
                return false;
            }

            imageCorners = poseData.GetPreferredBoardCornersImage();
            if (imageCorners == null || imageCorners.Length != 4)
            {
                Debug.LogError(
                    $"[{nameof(LensLabPoseBoardDebug)}] Preferred board image corners are not available in pose JSON. " +
                    "Re-run estimate_pose.py so the pose JSON contains board_model data.",
                    this
                );
                return false;
            }

            return true;
        }

        private void HideValidationGuideOverlay()
        {
            if (validationOverlay != null)
            {
                var type = validationOverlay.GetType();
                var principalField = type.GetField("showPrincipalPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var borderField = type.GetField("showImageBorder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                principalField?.SetValue(validationOverlay, false);
                borderField?.SetValue(validationOverlay, false);
            }
        }

        private void EnsureRuntimeObjects()
        {
            if (runtimeRoot != null && boardRenderer != null && boardMeshFilter != null)
            {
                return;
            }

            var existing = transform.Find(RuntimeRootName);
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
            }

            var rootObject = new GameObject(RuntimeRootName);
            rootObject.hideFlags = RuntimeHideFlags;
            rootObject.transform.SetParent(targetCamera != null ? targetCamera.transform : transform, false);
            rootObject.transform.localPosition = Vector3.zero;
            rootObject.transform.localRotation = Quaternion.identity;
            rootObject.transform.localScale = Vector3.one;
            runtimeRoot = rootObject.transform;

            var boardObject = new GameObject("BoardFill", typeof(MeshFilter), typeof(MeshRenderer));
            boardObject.hideFlags = RuntimeHideFlags;
            boardObject.transform.SetParent(runtimeRoot, false);
            boardMeshFilter = boardObject.GetComponent<MeshFilter>();
            boardRenderer = boardObject.GetComponent<MeshRenderer>();
            boardMaterial = new Material(Shader.Find("Unlit/Color"));
            boardMaterial.hideFlags = RuntimeHideFlags;
            boardRenderer.sharedMaterial = boardMaterial;
            boardRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            boardRenderer.receiveShadows = false;
            boardRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            boardRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            boardMesh = new Mesh { name = "LensLabPoseBoardProjectedMesh" };
            boardMesh.hideFlags = RuntimeHideFlags;
            boardMeshFilter.sharedMesh = boardMesh;
        }

        private void UpdateBoardGeometry(LensLabPoseData poseData, Vector2[] imageCorners)
        {
            if (boardRenderer == null || boardMeshFilter == null || boardMesh == null)
            {
                return;
            }

            boardMaterial.color = new Color(boardTint.r, boardTint.g, boardTint.b, boardOpacity);
            var worldCorners = BuildWorldCorners(poseData, imageCorners);
            var localCorners = new Vector3[worldCorners.Length];
            for (var i = 0; i < worldCorners.Length; i++)
            {
                localCorners[i] = runtimeRoot.InverseTransformPoint(worldCorners[i]);
            }

            boardMesh.Clear();
            boardMesh.vertices = localCorners;
            boardMesh.uv = new[]
            {
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f),
            };
            boardMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            boardMesh.RecalculateNormals();
            boardMesh.RecalculateBounds();
        }

        private Vector3[] BuildWorldCorners(LensLabPoseData poseData, Vector2[] imageCorners)
        {
            var renderDistance = ResolveRenderDistance();
            var corners = new Vector3[imageCorners.Length];
            for (var i = 0; i < imageCorners.Length; i++)
            {
                var viewportPoint = new Vector3(
                    Mathf.Clamp01(imageCorners[i].x / Mathf.Max(1, poseData.image_width)),
                    Mathf.Clamp01(1f - imageCorners[i].y / Mathf.Max(1, poseData.image_height)),
                    renderDistance
                );
                corners[i] = targetCamera.ViewportToWorldPoint(viewportPoint);
            }

            return corners;
        }

        private float ResolveRenderDistance()
        {
            if (validationOverlay != null)
            {
                return Mathf.Max(0.01f, validationOverlay.BackgroundDistance - renderDistanceOffset);
            }

            return 3f - renderDistanceOffset;
        }

        private string BuildDebugSummary(Vector3[] worldCorners)
        {
            return
                $"[{nameof(LensLabPoseBoardDebug)}] Refreshed board debug.\n" +
                $"Corner0: {worldCorners[0]}\n" +
                $"Corner1: {worldCorners[1]}\n" +
                $"Corner2: {worldCorners[2]}\n" +
                $"Corner3: {worldCorners[3]}";
        }

        private static bool CornersApproximatelyEqual(Vector3[] a, Vector3[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Length; i++)
            {
                if ((a[i] - b[i]).sqrMagnitude > 1e-8f)
                {
                    return false;
                }
            }

            return true;
        }

        private void OnDestroy()
        {
            if (boardMaterial != null)
            {
                DestroyImmediate(boardMaterial);
                boardMaterial = null;
            }

            if (boardMesh != null)
            {
                DestroyImmediate(boardMesh);
                boardMesh = null;
            }
        }
    }
}
