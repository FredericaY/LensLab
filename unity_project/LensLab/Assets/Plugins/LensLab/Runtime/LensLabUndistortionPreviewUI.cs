using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Runtime
{
    public class LensLabUndistortionPreviewUI : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabUndistortionController undistortionController;

        [Header("CPU Reference")]
        [SerializeField] private bool showCpuReference = true;
        [SerializeField] private string cpuReferenceResourcesPath = "LensLab/References/cpu_reference";

        [Header("Layout")]
        [SerializeField] private Vector2 canvasReferenceResolution = new Vector2(1920f, 1080f);
        [SerializeField] private Vector2 preferredPanelSize = new Vector2(420f, 340f);
        [SerializeField] private Vector2 minimumPanelSize = new Vector2(240f, 200f);
        [SerializeField] private float sidePadding = 24f;
        [SerializeField] private float outerMargin = 24f;
        [SerializeField] private float topLabelHeight = 48f;
        [SerializeField] private float panelInnerPadding = 12f;
        [SerializeField] private float headerSpacing = 14f;
        [SerializeField] private float bottomPadding = 12f;

        [Header("Behavior")]
        [SerializeField] private bool createOnAwake = true;
        [SerializeField] private bool rebuildEachPlay = true;
        [SerializeField] private bool runUndistortionAfterBuild = true;

        private Canvas runtimeCanvas;
        private RectTransform canvasRectTransform;
        private RectTransform inputPanelRect;
        private RectTransform cpuReferencePanelRect;
        private RectTransform outputPanelRect;
        private RectTransform inputFrameRect;
        private RectTransform cpuReferenceFrameRect;
        private RectTransform outputFrameRect;
        private RawImage inputRawImage;
        private RawImage cpuReferenceRawImage;
        private RawImage outputRawImage;
        private Texture2D cpuReferenceTexture;

        private void Reset()
        {
            if (undistortionController == null)
            {
                undistortionController = GetComponent<LensLabUndistortionController>();
            }
        }

        private void Awake()
        {
            if (undistortionController == null)
            {
                undistortionController = GetComponent<LensLabUndistortionController>();
            }

            if (createOnAwake)
            {
                BuildPreviewUI();
            }
        }

        [ContextMenu("Build Preview UI")]
        public void BuildPreviewUI()
        {
            if (undistortionController == null)
            {
                Debug.LogError($"[{nameof(LensLabUndistortionPreviewUI)}] Missing undistortion controller.", this);
                return;
            }

            LoadCpuReferenceTexture();

            if (rebuildEachPlay)
            {
                DestroyExistingCanvas();
            }
            else if (runtimeCanvas != null)
            {
                undistortionController.SetPreviewTargets(inputRawImage, outputRawImage);
                BindCpuReference();
                if (runUndistortionAfterBuild)
                {
                    undistortionController.RunUndistortion();
                }
                RefreshPreviewLayout();
                return;
            }

            runtimeCanvas = CreateCanvas();
            canvasRectTransform = runtimeCanvas.GetComponent<RectTransform>();

            bool useCpuReference = showCpuReference && cpuReferenceTexture != null;

            CreatePanel(runtimeCanvas.transform, "Original", out inputPanelRect, out inputFrameRect, out inputRawImage);

            if (useCpuReference)
            {
                CreatePanel(runtimeCanvas.transform, "CPU Reference", out cpuReferencePanelRect, out cpuReferenceFrameRect, out cpuReferenceRawImage);
            }
            else
            {
                cpuReferencePanelRect = null;
                cpuReferenceFrameRect = null;
                cpuReferenceRawImage = null;
            }

            CreatePanel(runtimeCanvas.transform, "GPU Result", out outputPanelRect, out outputFrameRect, out outputRawImage);

            undistortionController.SetPreviewTargets(inputRawImage, outputRawImage);
            BindCpuReference();

            if (runUndistortionAfterBuild)
            {
                undistortionController.RunUndistortion();
            }

            RefreshPreviewLayout();
        }

        private void LoadCpuReferenceTexture()
        {
            cpuReferenceTexture = null;
            if (!showCpuReference || string.IsNullOrWhiteSpace(cpuReferenceResourcesPath))
            {
                return;
            }

            cpuReferenceTexture = Resources.Load<Texture2D>(cpuReferenceResourcesPath);
            if (showCpuReference && cpuReferenceTexture == null)
            {
                Debug.LogWarning(
                    $"[{nameof(LensLabUndistortionPreviewUI)}] CPU reference texture not found at Resources path '{cpuReferenceResourcesPath}'.",
                    this
                );
            }
        }

        private void BindCpuReference()
        {
            if (cpuReferenceRawImage != null)
            {
                cpuReferenceRawImage.texture = cpuReferenceTexture != null ? cpuReferenceTexture : Texture2D.blackTexture;
            }
        }

        private void LateUpdate()
        {
            if (runtimeCanvas != null)
            {
                RefreshPreviewLayout();
            }
        }

        private void RefreshPreviewLayout()
        {
            if (canvasRectTransform == null)
            {
                return;
            }

            var panelCount = cpuReferencePanelRect != null ? 3 : 2;
            var canvasWidth = Mathf.Max(1f, canvasRectTransform.rect.width);
            var canvasHeight = Mathf.Max(1f, canvasRectTransform.rect.height);
            var totalSpacing = sidePadding * (panelCount - 1);
            var availableWidth = Mathf.Max(1f, canvasWidth - (outerMargin * 2f) - totalSpacing);
            var targetPanelWidth = Mathf.Min(preferredPanelSize.x, availableWidth / panelCount);
            targetPanelWidth = Mathf.Max(minimumPanelSize.x, targetPanelWidth);

            var maxPanelHeight = canvasHeight - (outerMargin * 2f) - topLabelHeight - headerSpacing - bottomPadding;
            var targetPanelHeight = Mathf.Min(preferredPanelSize.y, maxPanelHeight);
            targetPanelHeight = Mathf.Max(minimumPanelSize.y, targetPanelHeight);

            var totalPanelWidth = (targetPanelWidth * panelCount) + totalSpacing;
            var startX = -totalPanelWidth * 0.5f + targetPanelWidth * 0.5f;

            LayoutPanel(inputPanelRect, inputFrameRect, 0, startX, targetPanelWidth, targetPanelHeight);
            if (cpuReferencePanelRect != null)
            {
                LayoutPanel(cpuReferencePanelRect, cpuReferenceFrameRect, 1, startX, targetPanelWidth, targetPanelHeight);
                LayoutPanel(outputPanelRect, outputFrameRect, 2, startX, targetPanelWidth, targetPanelHeight);
            }
            else
            {
                LayoutPanel(outputPanelRect, outputFrameRect, 1, startX, targetPanelWidth, targetPanelHeight);
            }

            UpdateRawImageLayout(inputRawImage, inputFrameRect);
            UpdateRawImageLayout(cpuReferenceRawImage, cpuReferenceFrameRect);
            UpdateRawImageLayout(outputRawImage, outputFrameRect);
        }

        private void LayoutPanel(
            RectTransform panelRect,
            RectTransform frameRect,
            int index,
            float startX,
            float panelWidth,
            float panelHeight)
        {
            if (panelRect == null || frameRect == null)
            {
                return;
            }

            var x = startX + index * (panelWidth + sidePadding);
            var totalHeight = panelHeight + topLabelHeight + headerSpacing + bottomPadding;

            panelRect.sizeDelta = new Vector2(panelWidth, totalHeight);
            panelRect.anchoredPosition = new Vector2(x, 0f);

            frameRect.sizeDelta = new Vector2(panelWidth, panelHeight);
            frameRect.anchoredPosition = new Vector2(0f, bottomPadding);
        }

        private void UpdateRawImageLayout(RawImage rawImage, RectTransform frameRect)
        {
            if (rawImage == null || frameRect == null || rawImage.texture == null)
            {
                return;
            }

            var rawRect = rawImage.rectTransform;
            var textureWidth = rawImage.texture.width;
            var textureHeight = rawImage.texture.height;
            if (textureWidth <= 0 || textureHeight <= 0)
            {
                return;
            }

            var availableWidth = Mathf.Max(1f, frameRect.rect.width - (panelInnerPadding * 2f));
            var availableHeight = Mathf.Max(1f, frameRect.rect.height - (panelInnerPadding * 2f));
            var textureAspect = (float)textureWidth / textureHeight;
            var frameAspect = availableWidth / availableHeight;

            float fittedWidth;
            float fittedHeight;

            if (textureAspect >= frameAspect)
            {
                fittedWidth = availableWidth;
                fittedHeight = availableWidth / textureAspect;
            }
            else
            {
                fittedHeight = availableHeight;
                fittedWidth = availableHeight * textureAspect;
            }

            rawRect.sizeDelta = new Vector2(fittedWidth, fittedHeight);
            rawRect.anchoredPosition = Vector2.zero;
        }

        private void DestroyExistingCanvas()
        {
            if (runtimeCanvas == null)
            {
                return;
            }

            Destroy(runtimeCanvas.gameObject);
            runtimeCanvas = null;
            canvasRectTransform = null;
            inputPanelRect = null;
            cpuReferencePanelRect = null;
            outputPanelRect = null;
            inputFrameRect = null;
            cpuReferenceFrameRect = null;
            outputFrameRect = null;
            inputRawImage = null;
            cpuReferenceRawImage = null;
            outputRawImage = null;
        }

        private Canvas CreateCanvas()
        {
            var canvasObject = new GameObject("LensLabPreviewCanvas");
            canvasObject.transform.SetParent(transform, false);

            var rectTransform = canvasObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = canvasReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private void CreatePanel(
            Transform parent,
            string title,
            out RectTransform panelRect,
            out RectTransform frameRect,
            out RawImage rawImage)
        {
            var root = new GameObject(title + "Panel");
            root.transform.SetParent(parent, false);

            panelRect = root.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);

            var background = root.AddComponent<Image>();
            background.color = new Color(0.05f, 0.05f, 0.07f, 0.92f);

            var label = CreateLabel(root.transform, title);
            label.rectTransform.anchoredPosition = new Vector2(0f, -10f);

            var frame = new GameObject(title + "Frame");
            frame.transform.SetParent(root.transform, false);
            frameRect = frame.AddComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0.5f, 0f);
            frameRect.anchorMax = new Vector2(0.5f, 0f);
            frameRect.pivot = new Vector2(0.5f, 0f);

            var frameImage = frame.AddComponent<Image>();
            frameImage.color = new Color(0.12f, 0.12f, 0.14f, 1f);

            rawImage = new GameObject(title + "RawImage").AddComponent<RawImage>();
            rawImage.transform.SetParent(frame.transform, false);
            rawImage.color = Color.white;
            rawImage.texture = Texture2D.blackTexture;
            rawImage.raycastTarget = false;

            var rawRect = rawImage.rectTransform;
            rawRect.anchorMin = new Vector2(0.5f, 0.5f);
            rawRect.anchorMax = new Vector2(0.5f, 0.5f);
            rawRect.pivot = new Vector2(0.5f, 0.5f);
            rawRect.anchoredPosition = Vector2.zero;
        }

        private Text CreateLabel(Transform parent, string textValue)
        {
            var labelObject = new GameObject(textValue + "Label");
            labelObject.transform.SetParent(parent, false);

            var text = labelObject.AddComponent<Text>();
            text.text = textValue;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 24;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;

            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(preferredPanelSize.x, topLabelHeight);

            return text;
        }
    }
}
