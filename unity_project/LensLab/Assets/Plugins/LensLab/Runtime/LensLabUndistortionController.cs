using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Runtime
{
    public class LensLabUndistortionController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;
        [SerializeField] private ComputeShader undistortionShader;

        [Header("Input")]
        [SerializeField] private Texture inputTexture;
        [SerializeField] [Range(0f, 1f)] private float outputAlpha = 0f;

        [Header("Optional Preview Targets")]
        [SerializeField] private RawImage inputPreview;
        [SerializeField] private RawImage outputPreview;
        [SerializeField] private Renderer outputRenderer;

        [Header("Behavior")]
        [SerializeField] private bool runOnStart = true;
        [SerializeField] private bool runEveryFrame = false;
        [SerializeField] private bool verboseLogging = true;

        private const string KernelName = "CSMain";
        private RenderTexture outputTexture;
        private int kernelIndex = -1;
        private Texture lastLoggedInputTexture;
        private Vector2Int lastLoggedSize;
        private float lastLoggedAlpha = -1f;

        public RenderTexture OutputTexture => outputTexture;
        public Texture InputTexture => inputTexture;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void Awake()
        {
            TryAutoAssignDependencies();
        }

        private void Start()
        {
            if (runOnStart)
            {
                RunUndistortion();
            }
        }

        private void LateUpdate()
        {
            if (runEveryFrame)
            {
                RunUndistortion();
            }
        }

        [ContextMenu("Run Undistortion")]
        public void RunUndistortion()
        {
            if (!ValidateSetup())
            {
                return;
            }

            var calibration = calibrationLoader.LoadedCalibration;
            var width = inputTexture.width;
            var height = inputTexture.height;

            EnsureOutputTexture(width, height);
            EnsureKernelIndex();

            var intrinsics = calibration.intrinsics;
            var scaleX = calibration.image_width > 0 ? (float)width / calibration.image_width : 1f;
            var scaleY = calibration.image_height > 0 ? (float)height / calibration.image_height : 1f;

            var scaledFx = (float)intrinsics.fx * scaleX;
            var scaledFy = (float)intrinsics.fy * scaleY;
            var scaledCx = (float)intrinsics.cx * scaleX;
            var scaledCy = (float)intrinsics.cy * scaleY;

            var distortion = calibration.distortion_coeffs;

            undistortionShader.SetTexture(kernelIndex, "_InputTexture", inputTexture);
            undistortionShader.SetTexture(kernelIndex, "_OutputTexture", outputTexture);
            undistortionShader.SetVector("_CameraIntrinsics", new Vector4(scaledFx, scaledFy, scaledCx, scaledCy));
            undistortionShader.SetVector(
                "_Distortion0",
                new Vector4((float)distortion.k1, (float)distortion.k2, (float)distortion.p1, (float)distortion.p2)
            );
            undistortionShader.SetVector(
                "_Distortion1",
                new Vector4((float)distortion.k3, (float)distortion.k4, (float)distortion.k5, (float)distortion.k6)
            );
            undistortionShader.SetVector(
                "_ImageSize",
                new Vector4(width, height, 1f / width, 1f / height)
            );
            undistortionShader.SetFloat("_OutputAlpha", outputAlpha);

            var dispatchX = Mathf.CeilToInt(width / 8f);
            var dispatchY = Mathf.CeilToInt(height / 8f);
            undistortionShader.Dispatch(kernelIndex, dispatchX, dispatchY, 1);

            BindPreviewTargets();

            if (ShouldLogDispatch(width, height))
            {
                Debug.Log(BuildDebugSummary(width, height, scaledFx, scaledFy, scaledCx, scaledCy), this);
                lastLoggedInputTexture = inputTexture;
                lastLoggedSize = new Vector2Int(width, height);
                lastLoggedAlpha = outputAlpha;
            }
        }

        public void SetInputTexture(Texture texture, bool runNow = true)
        {
            if (inputTexture == texture)
            {
                if (runNow)
                {
                    RunUndistortion();
                }
                return;
            }

            inputTexture = texture;
            if (runNow)
            {
                RunUndistortion();
            }
        }

        public void SetPreviewTargets(RawImage inputPreviewTarget, RawImage outputPreviewTarget)
        {
            inputPreview = inputPreviewTarget;
            outputPreview = outputPreviewTarget;
            BindPreviewTargets();
        }

        public void SetOutputRenderer(Renderer rendererTarget)
        {
            outputRenderer = rendererTarget;
            BindPreviewTargets();
        }

        private void TryAutoAssignDependencies()
        {
            if (calibrationLoader == null)
            {
                calibrationLoader = GetComponent<LensLabCalibrationLoader>();
            }
        }

        private bool ValidateSetup()
        {
            TryAutoAssignDependencies();

            if (calibrationLoader == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabUndistortionController)}] Missing calibration loader. " +
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
                    Debug.LogError($"[{nameof(LensLabUndistortionController)}] Calibration data is not available.", this);
                    return false;
                }
            }

            if (undistortionShader == null)
            {
                Debug.LogError($"[{nameof(LensLabUndistortionController)}] Missing compute shader reference.", this);
                return false;
            }

            if (inputTexture == null)
            {
                Debug.LogError($"[{nameof(LensLabUndistortionController)}] Missing input texture.", this);
                return false;
            }

            return true;
        }

        private void EnsureKernelIndex()
        {
            if (kernelIndex < 0)
            {
                kernelIndex = undistortionShader.FindKernel(KernelName);
            }
        }

        private void EnsureOutputTexture(int width, int height)
        {
            if (outputTexture != null && outputTexture.width == width && outputTexture.height == height)
            {
                return;
            }

            ReleaseOutputTexture();

            outputTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "LensLabUndistortedOutput",
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            outputTexture.Create();
        }

        private void BindPreviewTargets()
        {
            if (inputPreview != null)
            {
                inputPreview.texture = inputTexture;
            }

            if (outputPreview != null)
            {
                outputPreview.texture = outputTexture;
            }

            if (outputRenderer != null && outputRenderer.sharedMaterial != null)
            {
                outputRenderer.sharedMaterial.mainTexture = outputTexture;
            }
        }

        private string BuildDebugSummary(int width, int height, float fx, float fy, float cx, float cy)
        {
            var calibration = calibrationLoader.LoadedCalibration;
            return
                $"[{nameof(LensLabUndistortionController)}] GPU undistortion dispatched.\n" +
                $"Input Size: {width}x{height}\n" +
                $"Calibration Size: {calibration.image_width}x{calibration.image_height}\n" +
                $"Scaled Intrinsics: fx={fx:F3}, fy={fy:F3}, cx={cx:F3}, cy={cy:F3}\n" +
                $"Output Alpha: {outputAlpha:F2}";
        }

        private bool ShouldLogDispatch(int width, int height)
        {
            if (!verboseLogging)
            {
                return false;
            }

            return inputTexture != lastLoggedInputTexture
                || lastLoggedSize.x != width
                || lastLoggedSize.y != height
                || !Mathf.Approximately(lastLoggedAlpha, outputAlpha);
        }

        private void OnDestroy()
        {
            ReleaseOutputTexture();
        }

        private void ReleaseOutputTexture()
        {
            if (outputTexture == null)
            {
                return;
            }

            if (outputTexture.IsCreated())
            {
                outputTexture.Release();
            }

            Destroy(outputTexture);
            outputTexture = null;
        }
    }
}

