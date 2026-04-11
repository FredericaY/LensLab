using UnityEngine;

namespace LensLab.Runtime
{
    [ExecuteAlways]
    [RequireComponent(typeof(Camera))]
    public class LensLabCameraProjectionController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;
        [SerializeField] private Camera targetCamera;

        [Header("Projection")]
        [SerializeField] private bool applyOnEnable = true;
        [SerializeField] private bool keepProjectionUpdated = true;
        [SerializeField] private bool principalPointIsTopLeft = true;
        [SerializeField] private bool useCameraPixelRect = true;
        [SerializeField] private Vector2Int overrideImageSize = Vector2Int.zero;

        [Header("Clipping")]
        [SerializeField] private float nearClip = 0.01f;
        [SerializeField] private float farClip = 1000f;

        [Header("Behavior")]
        [SerializeField] private bool verboseLogging = true;

        private Matrix4x4 lastAppliedProjection;
        private Vector2Int lastAppliedImageSize;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void OnEnable()
        {
            TryAutoAssignDependencies();

            if (applyOnEnable)
            {
                ApplyProjection();
            }
        }

        private void LateUpdate()
        {
            if (!keepProjectionUpdated)
            {
                return;
            }

            ApplyProjection();
        }

        [ContextMenu("Apply Projection")]
        public void ApplyProjection()
        {
            if (!ValidateSetup(out var calibration))
            {
                return;
            }

            var imageSize = ResolveTargetImageSize(calibration);
            if (imageSize.x <= 0 || imageSize.y <= 0)
            {
                Debug.LogError($"[{nameof(LensLabCameraProjectionController)}] Invalid target image size.", this);
                return;
            }

            var projection = BuildProjectionMatrix(
                calibration: calibration,
                imageWidth: imageSize.x,
                imageHeight: imageSize.y,
                near: nearClip,
                far: farClip,
                principalPointIsTopLeft: principalPointIsTopLeft
            );

            targetCamera.usePhysicalProperties = false;
            targetCamera.nearClipPlane = nearClip;
            targetCamera.farClipPlane = farClip;
            targetCamera.projectionMatrix = projection;

            var shouldLog = verboseLogging
                && (projection != lastAppliedProjection || imageSize != lastAppliedImageSize);

            lastAppliedProjection = projection;
            lastAppliedImageSize = imageSize;

            if (shouldLog)
            {
                Debug.Log(BuildDebugSummary(calibration, imageSize, projection), this);
            }
        }

        [ContextMenu("Reset To Camera Defaults")]
        public void ResetProjection()
        {
            TryAutoAssignDependencies();

            if (targetCamera == null)
            {
                return;
            }

            targetCamera.ResetProjectionMatrix();
        }

        public static Matrix4x4 BuildProjectionMatrix(
            LensLabCalibrationData calibration,
            int imageWidth,
            int imageHeight,
            float near,
            float far,
            bool principalPointIsTopLeft
        )
        {
            var intrinsics = calibration.intrinsics;
            var scaleX = calibration.image_width > 0 ? (float)imageWidth / calibration.image_width : 1f;
            var scaleY = calibration.image_height > 0 ? (float)imageHeight / calibration.image_height : 1f;

            var fx = (float)intrinsics.fx * scaleX;
            var fy = (float)intrinsics.fy * scaleY;
            var cx = (float)intrinsics.cx * scaleX;
            var cy = (float)intrinsics.cy * scaleY;

            var matrix = Matrix4x4.zero;
            matrix[0, 0] = 2f * fx / imageWidth;
            matrix[0, 2] = 1f - (2f * cx / imageWidth);

            matrix[1, 1] = 2f * fy / imageHeight;
            matrix[1, 2] = principalPointIsTopLeft
                ? (2f * cy / imageHeight) - 1f
                : 1f - (2f * cy / imageHeight);

            matrix[2, 2] = -(far + near) / (far - near);
            matrix[2, 3] = -(2f * far * near) / (far - near);
            matrix[3, 2] = -1f;
            return matrix;
        }

        private void TryAutoAssignDependencies()
        {
            if (calibrationLoader == null)
            {
                calibrationLoader = GetComponent<LensLabCalibrationLoader>();
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
                    $"[{nameof(LensLabCameraProjectionController)}] Missing calibration loader. " +
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
                    Debug.LogError($"[{nameof(LensLabCameraProjectionController)}] Calibration data is not available.", this);
                    return false;
                }
            }

            if (targetCamera == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabCameraProjectionController)}] Missing target camera. " +
                    "Add this component to a Camera or assign one explicitly.",
                    this
                );
                return false;
            }

            if (nearClip <= 0f || farClip <= nearClip)
            {
                Debug.LogError($"[{nameof(LensLabCameraProjectionController)}] Invalid near/far clip configuration.", this);
                return false;
            }

            calibration = calibrationLoader.LoadedCalibration;
            return calibration != null && calibration.IsValid();
        }

        private Vector2Int ResolveTargetImageSize(LensLabCalibrationData calibration)
        {
            if (overrideImageSize.x > 0 && overrideImageSize.y > 0)
            {
                return overrideImageSize;
            }

            if (useCameraPixelRect && targetCamera != null)
            {
                var pixelWidth = Mathf.RoundToInt(targetCamera.pixelRect.width);
                var pixelHeight = Mathf.RoundToInt(targetCamera.pixelRect.height);
                if (pixelWidth > 0 && pixelHeight > 0)
                {
                    return new Vector2Int(pixelWidth, pixelHeight);
                }
            }

            return new Vector2Int(calibration.image_width, calibration.image_height);
        }

        private string BuildDebugSummary(LensLabCalibrationData calibration, Vector2Int imageSize, Matrix4x4 projection)
        {
            var intrinsics = calibration.intrinsics;
            var scaleX = calibration.image_width > 0 ? (float)imageSize.x / calibration.image_width : 1f;
            var scaleY = calibration.image_height > 0 ? (float)imageSize.y / calibration.image_height : 1f;
            var fx = (float)intrinsics.fx * scaleX;
            var fy = (float)intrinsics.fy * scaleY;
            var cx = (float)intrinsics.cx * scaleX;
            var cy = (float)intrinsics.cy * scaleY;
            var verticalFov = 2f * Mathf.Atan(imageSize.y / (2f * fy)) * Mathf.Rad2Deg;
            var horizontalFov = 2f * Mathf.Atan(imageSize.x / (2f * fx)) * Mathf.Rad2Deg;

            return
                $"[{nameof(LensLabCameraProjectionController)}] Applied calibrated projection to '{targetCamera.name}'.\n" +
                $"Target Size: {imageSize.x}x{imageSize.y}\n" +
                $"Scaled Intrinsics: fx={fx:F3}, fy={fy:F3}, cx={cx:F3}, cy={cy:F3}\n" +
                $"Approx FOV: horizontal={horizontalFov:F3}, vertical={verticalFov:F3}\n" +
                $"Projection m02={projection[0, 2]:F6}, m12={projection[1, 2]:F6}";
        }
    }
}
