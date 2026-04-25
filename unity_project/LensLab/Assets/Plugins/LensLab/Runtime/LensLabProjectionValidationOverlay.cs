using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Runtime
{
    [ExecuteAlways]
    public class LensLabProjectionValidationOverlay : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;
        [SerializeField] private LensLabCameraProjectionController projectionController;
        [SerializeField] private Camera targetCamera;

        [Header("Background")]
        [SerializeField] private Texture backgroundTexture;
        [SerializeField] private string backgroundResourcesPath = "LensLab/References/pose_reference";
        [SerializeField] private bool loadBackgroundFromResourcesIfMissing = true;
        [SerializeField] private float backgroundDistance = 3f;
        [SerializeField] private bool renderBackgroundInCanvas = false;
        [SerializeField] private bool renderBackgroundInWorldSpace = true;

        [Header("Layout")]
        [SerializeField] private bool matchCameraViewportToCalibrationAspect = true;
        [SerializeField] private float screenPadding = 48f;
        [SerializeField] private int canvasSortingOrder = 100;

        [Header("Overlay")]
        [SerializeField] private bool showPrincipalPoint = false;
        [SerializeField] private bool showImageBorder = false;
        [SerializeField] private Color borderColor = new Color(0.85f, 0.9f, 1f, 0.9f);
        [SerializeField] private Color principalPointColor = new Color(1f, 0.45f, 0.2f, 0.95f);
        [SerializeField] private Vector2 principalPointMarkerSize = new Vector2(32f, 32f);
        [SerializeField] private float borderThickness = 3f;

        [Header("Behavior")]
        [SerializeField] private bool suppressLegacyPreviewUI = true;
        [SerializeField] private bool createOnEnable = true;
        [SerializeField] private bool rebuildEveryFrame = true;
        [SerializeField] private bool verboseLogging = true;

        private const string OverlayRootName = "LensLabProjectionValidationOverlay_Runtime";
        private const string BackgroundQuadName = "LensLabProjectionValidationBackground_Runtime";
        private const HideFlags RuntimeHideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

        private Canvas overlayCanvas;
        private RectTransform overlayRoot;
        private RectTransform frameRect;
        private RectTransform principalHorizontal;
        private RectTransform principalVertical;
        private Image[] borderImages;
        private RawImage backgroundRawImage;
        private Transform backgroundQuadRoot;
        private MeshRenderer backgroundRenderer;
        private MeshFilter backgroundMeshFilter;
        private Material backgroundMaterial;
        private Mesh backgroundMesh;
        private Rect lastViewportPixelRect = Rect.zero;

        public Camera TargetCamera => targetCamera;
        public float BackgroundDistance => backgroundDistance;
        public Texture BackgroundTexture => backgroundTexture;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void OnEnable()
        {
            TryAutoAssignDependencies();
            if (createOnEnable)
            {
                RefreshOverlay();
            }
        }

        private void LateUpdate()
        {
            if (!rebuildEveryFrame)
            {
                return;
            }

            RefreshOverlay();
        }

        [ContextMenu("Refresh Overlay")]
        public void RefreshOverlay()
        {
            if (!ValidateSetup(out var calibration))
            {
                return;
            }

            SuppressLegacyPreviewUI();

            var viewportPixelRect = ResolveViewportPixelRect(calibration);
            EnsureOverlayHierarchy();
            EnsureBackgroundQuad();
            ApplyViewportRect(viewportPixelRect, calibration);
            UpdateCanvasBackground();
            UpdateBackgroundQuad();

            if (verboseLogging && viewportPixelRect != lastViewportPixelRect)
            {
                Debug.Log(BuildDebugSummary(calibration, viewportPixelRect), this);
            }

            lastViewportPixelRect = viewportPixelRect;
        }

        public void SetBackgroundTexture(Texture texture, bool refreshNow = true)
        {
            backgroundTexture = texture;
            if (texture != null)
            {
                loadBackgroundFromResourcesIfMissing = false;
            }

            if (refreshNow)
            {
                RefreshOverlay();
            }
        }

        public void SetBackgroundRenderMode(bool useCanvasBackground, bool useWorldBackground, bool refreshNow = true)
        {
            renderBackgroundInCanvas = useCanvasBackground;
            renderBackgroundInWorldSpace = useWorldBackground;
            if (refreshNow)
            {
                RefreshOverlay();
            }
        }

        private void SuppressLegacyPreviewUI()
        {
            if (!suppressLegacyPreviewUI)
            {
                return;
            }

            var legacyCanvas = GameObject.Find("LensLabPreviewCanvas");
            if (legacyCanvas != null)
            {
                legacyCanvas.SetActive(false);
            }
        }

        private void TryAutoAssignDependencies()
        {
            if (calibrationLoader == null)
            {
                calibrationLoader = GetComponent<LensLabCalibrationLoader>();
            }

            if (projectionController == null)
            {
                projectionController = GetComponent<LensLabCameraProjectionController>();
            }

            if (targetCamera == null)
            {
                targetCamera = GetComponent<Camera>();
            }
        }

        private bool ValidateSetup(out LensLabCalibrationData calibration)
        {
            calibration = null;
            TryAutoAssignDependencies();

            if (calibrationLoader == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabProjectionValidationOverlay)}] Missing calibration loader. " +
                    "Assign one explicitly or add LensLabCalibrationLoader to the same GameObject.",
                    this
                );
                return false;
            }

            if (!calibrationLoader.HasValidCalibration)
            {
                calibrationLoader.LoadCalibration();
                if (!calibrationLoader.HasValidCalibration)
                {
                    Debug.LogError($"[{nameof(LensLabProjectionValidationOverlay)}] Calibration data is not available.", this);
                    return false;
                }
            }

            if (targetCamera == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabProjectionValidationOverlay)}] Missing target camera. " +
                    "Add this component to a Camera or assign one explicitly.",
                    this
                );
                return false;
            }

            if (backgroundTexture == null && loadBackgroundFromResourcesIfMissing)
            {
                backgroundTexture = Resources.Load<Texture>(backgroundResourcesPath);
            }

            if (backgroundTexture == null)
            {
                Debug.LogWarning(
                    $"[{nameof(LensLabProjectionValidationOverlay)}] No background texture found. " +
                    "Assign one explicitly or provide a valid Resources path.",
                    this
                );
            }

            calibration = calibrationLoader.LoadedCalibration;
            return calibration != null && calibration.IsValid();
        }

        private Rect ResolveViewportPixelRect(LensLabCalibrationData calibration)
        {
            var screenWidth = Mathf.Max(1f, Screen.width);
            var screenHeight = Mathf.Max(1f, Screen.height);
            var availableWidth = Mathf.Max(1f, screenWidth - screenPadding * 2f);
            var availableHeight = Mathf.Max(1f, screenHeight - screenPadding * 2f);
            var aspect = calibration.image_height > 0
                ? (float)calibration.image_width / calibration.image_height
                : screenWidth / screenHeight;

            var width = availableWidth;
            var height = width / aspect;
            if (height > availableHeight)
            {
                height = availableHeight;
                width = height * aspect;
            }

            var x = (screenWidth - width) * 0.5f;
            var y = (screenHeight - height) * 0.5f;
            var pixelRect = new Rect(x, y, width, height);

            if (matchCameraViewportToCalibrationAspect && targetCamera != null)
            {
                targetCamera.rect = new Rect(
                    pixelRect.x / screenWidth,
                    pixelRect.y / screenHeight,
                    pixelRect.width / screenWidth,
                    pixelRect.height / screenHeight
                );
            }
            else if (targetCamera != null)
            {
                targetCamera.rect = new Rect(0f, 0f, 1f, 1f);
            }

            return pixelRect;
        }

        private void EnsureOverlayHierarchy()
        {
            if (overlayCanvas != null && overlayRoot != null && frameRect != null)
            {
                return;
            }

            var existingRoot = transform.Find(OverlayRootName);
            if (existingRoot != null)
            {
                DestroyImmediate(existingRoot.gameObject);
            }

            var rootObject = new GameObject(OverlayRootName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            rootObject.hideFlags = RuntimeHideFlags;
            rootObject.transform.SetParent(transform, false);

            overlayRoot = rootObject.GetComponent<RectTransform>();
            overlayRoot.anchorMin = Vector2.zero;
            overlayRoot.anchorMax = Vector2.one;
            overlayRoot.offsetMin = Vector2.zero;
            overlayRoot.offsetMax = Vector2.zero;

            overlayCanvas = rootObject.GetComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = canvasSortingOrder;

            var scaler = rootObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            frameRect = CreateRect("ImageFrame", overlayRoot, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);

            backgroundRawImage = CreateBackgroundImage("BackgroundImage", frameRect);

            borderImages = new Image[4];
            borderImages[0] = CreateBorder("BorderTop", frameRect);
            borderImages[1] = CreateBorder("BorderBottom", frameRect);
            borderImages[2] = CreateBorder("BorderLeft", frameRect);
            borderImages[3] = CreateBorder("BorderRight", frameRect);

            principalHorizontal = CreateMarker("PrincipalHorizontal", frameRect);
            principalVertical = CreateMarker("PrincipalVertical", frameRect);
        }

        private void EnsureBackgroundQuad()
        {
            if (backgroundQuadRoot != null && backgroundRenderer != null && backgroundMeshFilter != null)
            {
                return;
            }

            var existing = transform.Find(BackgroundQuadName);
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
            }

            var quadObject = new GameObject(BackgroundQuadName, typeof(MeshFilter), typeof(MeshRenderer));
            quadObject.hideFlags = RuntimeHideFlags;
            quadObject.transform.SetParent(targetCamera != null ? targetCamera.transform : transform, false);
            backgroundQuadRoot = quadObject.transform;

            backgroundMeshFilter = quadObject.GetComponent<MeshFilter>();
            backgroundRenderer = quadObject.GetComponent<MeshRenderer>();

            backgroundMaterial = new Material(Shader.Find("Unlit/Texture"));
            backgroundMaterial.hideFlags = RuntimeHideFlags;
            backgroundRenderer.sharedMaterial = backgroundMaterial;
            backgroundMesh = new Mesh { name = "LensLabProjectionValidationBackgroundMesh" };
            backgroundMesh.hideFlags = RuntimeHideFlags;
            backgroundMeshFilter.sharedMesh = backgroundMesh;
            backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            backgroundRenderer.receiveShadows = false;
            backgroundRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            backgroundRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private void ApplyViewportRect(Rect viewportPixelRect, LensLabCalibrationData calibration)
        {
            frameRect.anchorMin = Vector2.zero;
            frameRect.anchorMax = Vector2.zero;
            frameRect.pivot = new Vector2(0f, 0f);
            frameRect.anchoredPosition = new Vector2(viewportPixelRect.x, viewportPixelRect.y);
            frameRect.sizeDelta = new Vector2(viewportPixelRect.width, viewportPixelRect.height);

            ApplyBorders(viewportPixelRect.size);
            ApplyPrincipalPoint(viewportPixelRect.size, calibration);
        }

        private void UpdateBackgroundQuad()
        {
            if (backgroundQuadRoot == null || backgroundRenderer == null || backgroundMeshFilter == null)
            {
                return;
            }

            var shouldShow = renderBackgroundInWorldSpace && !renderBackgroundInCanvas && targetCamera != null && backgroundTexture != null;
            backgroundRenderer.enabled = shouldShow;
            if (!shouldShow)
            {
                return;
            }

            backgroundMaterial.mainTexture = backgroundTexture;
            backgroundQuadRoot.localPosition = new Vector3(0f, 0f, backgroundDistance);
            backgroundQuadRoot.localRotation = Quaternion.identity;
            backgroundQuadRoot.localScale = Vector3.one;

            var bl = targetCamera.ViewportToWorldPoint(new Vector3(0f, 0f, backgroundDistance));
            var tl = targetCamera.ViewportToWorldPoint(new Vector3(0f, 1f, backgroundDistance));
            var tr = targetCamera.ViewportToWorldPoint(new Vector3(1f, 1f, backgroundDistance));
            var br = targetCamera.ViewportToWorldPoint(new Vector3(1f, 0f, backgroundDistance));

            var localBl = backgroundQuadRoot.InverseTransformPoint(bl);
            var localTl = backgroundQuadRoot.InverseTransformPoint(tl);
            var localTr = backgroundQuadRoot.InverseTransformPoint(tr);
            var localBr = backgroundQuadRoot.InverseTransformPoint(br);

            backgroundMesh.Clear();
            backgroundMesh.vertices = new[] { localBl, localTl, localTr, localBr };
            backgroundMesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            backgroundMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            backgroundMesh.RecalculateNormals();
            backgroundMesh.RecalculateBounds();
        }

        private void UpdateCanvasBackground()
        {
            if (backgroundRawImage == null)
            {
                return;
            }

            backgroundRawImage.enabled = renderBackgroundInCanvas && backgroundTexture != null;
            backgroundRawImage.texture = backgroundTexture;
            backgroundRawImage.color = Color.white;
            backgroundRawImage.rectTransform.anchorMin = Vector2.zero;
            backgroundRawImage.rectTransform.anchorMax = Vector2.one;
            backgroundRawImage.rectTransform.offsetMin = Vector2.zero;
            backgroundRawImage.rectTransform.offsetMax = Vector2.zero;
        }

        private void ApplyBorders(Vector2 frameSize)
        {
            if (borderImages == null || borderImages.Length != 4)
            {
                return;
            }

            foreach (var border in borderImages)
            {
                border.enabled = showImageBorder;
                border.color = borderColor;
            }

            borderImages[0].rectTransform.anchorMin = new Vector2(0f, 1f);
            borderImages[0].rectTransform.anchorMax = new Vector2(1f, 1f);
            borderImages[0].rectTransform.pivot = new Vector2(0.5f, 1f);
            borderImages[0].rectTransform.sizeDelta = new Vector2(0f, borderThickness);
            borderImages[0].rectTransform.anchoredPosition = Vector2.zero;

            borderImages[1].rectTransform.anchorMin = new Vector2(0f, 0f);
            borderImages[1].rectTransform.anchorMax = new Vector2(1f, 0f);
            borderImages[1].rectTransform.pivot = new Vector2(0.5f, 0f);
            borderImages[1].rectTransform.sizeDelta = new Vector2(0f, borderThickness);
            borderImages[1].rectTransform.anchoredPosition = Vector2.zero;

            borderImages[2].rectTransform.anchorMin = new Vector2(0f, 0f);
            borderImages[2].rectTransform.anchorMax = new Vector2(0f, 1f);
            borderImages[2].rectTransform.pivot = new Vector2(0f, 0.5f);
            borderImages[2].rectTransform.sizeDelta = new Vector2(borderThickness, 0f);
            borderImages[2].rectTransform.anchoredPosition = Vector2.zero;

            borderImages[3].rectTransform.anchorMin = new Vector2(1f, 0f);
            borderImages[3].rectTransform.anchorMax = new Vector2(1f, 1f);
            borderImages[3].rectTransform.pivot = new Vector2(1f, 0.5f);
            borderImages[3].rectTransform.sizeDelta = new Vector2(borderThickness, 0f);
            borderImages[3].rectTransform.anchoredPosition = Vector2.zero;
        }

        private void ApplyPrincipalPoint(Vector2 frameSize, LensLabCalibrationData calibration)
        {
            if (principalHorizontal == null || principalVertical == null)
            {
                return;
            }

            var px = calibration.image_width > 0
                ? (float)calibration.intrinsics.cx / calibration.image_width * frameSize.x
                : frameSize.x * 0.5f;
            var pyFromTop = calibration.image_height > 0
                ? (float)calibration.intrinsics.cy / calibration.image_height * frameSize.y
                : frameSize.y * 0.5f;
            var anchoredY = frameSize.y - pyFromTop;

            principalHorizontal.gameObject.SetActive(showPrincipalPoint);
            principalVertical.gameObject.SetActive(showPrincipalPoint);

            var horizontalImage = principalHorizontal.GetComponent<Image>();
            var verticalImage = principalVertical.GetComponent<Image>();
            horizontalImage.color = principalPointColor;
            verticalImage.color = principalPointColor;

            principalHorizontal.anchorMin = new Vector2(0f, 0f);
            principalHorizontal.anchorMax = new Vector2(0f, 0f);
            principalHorizontal.pivot = new Vector2(0.5f, 0.5f);
            principalHorizontal.sizeDelta = new Vector2(principalPointMarkerSize.x, borderThickness);
            principalHorizontal.anchoredPosition = new Vector2(px, anchoredY);

            principalVertical.anchorMin = new Vector2(0f, 0f);
            principalVertical.anchorMax = new Vector2(0f, 0f);
            principalVertical.pivot = new Vector2(0.5f, 0.5f);
            principalVertical.sizeDelta = new Vector2(borderThickness, principalPointMarkerSize.y);
            principalVertical.anchoredPosition = new Vector2(px, anchoredY);
        }

        private static RectTransform CreateRect(string name, Transform parent, Vector2 anchoredPosition, Vector2 pivot, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.hideFlags = RuntimeHideFlags;
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            return rect;
        }

        private static Image CreateBorder(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.hideFlags = RuntimeHideFlags;
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private static RawImage CreateBackgroundImage(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            go.hideFlags = RuntimeHideFlags;
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<RawImage>();
            image.raycastTarget = false;
            return image;
        }

        private static RectTransform CreateMarker(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.hideFlags = RuntimeHideFlags;
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return go.GetComponent<RectTransform>();
        }

        private string BuildDebugSummary(LensLabCalibrationData calibration, Rect viewportPixelRect)
        {
            return
                $"[{nameof(LensLabProjectionValidationOverlay)}] Refreshed projection overlay.\n" +
                $"Viewport: x={viewportPixelRect.x:F1}, y={viewportPixelRect.y:F1}, w={viewportPixelRect.width:F1}, h={viewportPixelRect.height:F1}\n" +
                $"Calibration Size: {calibration.image_width}x{calibration.image_height}";
        }

        private void OnDestroy()
        {
            if (backgroundMaterial != null)
            {
                DestroyImmediate(backgroundMaterial);
                backgroundMaterial = null;
            }

            if (backgroundMesh != null)
            {
                DestroyImmediate(backgroundMesh);
                backgroundMesh = null;
            }
        }
    }
}
