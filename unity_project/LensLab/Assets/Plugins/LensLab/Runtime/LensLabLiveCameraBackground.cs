using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Binds the TCP live camera feed to the calibrated background plane.
    ///
    /// Python owns the webcam and streams JPEG frames over TCP through
    /// LensLabPoseClient. Unity displays the latest received texture and lets
    /// LensLabLivePoseReceiver anchor virtual content from the matching pose.
    /// </summary>
    public class LensLabLiveCameraBackground : MonoBehaviour
    {
        [Header("Frame Source")]
        [SerializeField] private LensLabPoseClient poseClient;
        [SerializeField] private LensLabProjectionValidationOverlay validationOverlay;
        [SerializeField] private LensLabUndistortionController undistortionController;
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;

        [Header("Background Mode")]
        [SerializeField] private bool useGpuUndistortion = false;
        [SerializeField] private bool runUndistortionEveryCameraFrame = true;
        [SerializeField] private bool useCanvasBackgroundForRawLiveTest = false;

        [Header("Diagnostics")]
        [SerializeField] private bool warnIfResolutionDiffersFromCalibration = false;
        [SerializeField] private bool verboseLogging = true;

        private Texture lastAppliedTexture;
        private Vector2Int lastReportedSize;
        private bool warnedAboutMissingDependencies;
        private bool warnedAboutWaitingForFrames;
        private bool warnedAboutMissingUndistortionController;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void Awake()
        {
            TryAutoAssignDependencies();
        }

        private void LateUpdate()
        {
            UpdateLiveBackground();
        }

        [ContextMenu("Update Live Background")]
        public void UpdateLiveBackground()
        {
            TryAutoAssignDependencies();

            if (validationOverlay == null || poseClient == null)
            {
                WarnAboutMissingDependenciesOnce();
                return;
            }

            var sourceTexture = ResolveSourceTexture();
            if (sourceTexture == null)
            {
                WarnAboutWaitingForFramesOnce();
                return;
            }

            warnedAboutWaitingForFrames = false;
            WarnAboutResolutionMismatchOnce(sourceTexture);

            var texture = useGpuUndistortion
                ? RunUndistortion(sourceTexture)
                : sourceTexture;

            if (texture == null || texture == lastAppliedTexture)
            {
                return;
            }

            validationOverlay.SetBackgroundRenderMode(
                useCanvasBackgroundForRawLiveTest,
                !useCanvasBackgroundForRawLiveTest,
                false
            );
            validationOverlay.SetBackgroundTexture(texture, true);
            lastAppliedTexture = texture;

            if (verboseLogging)
            {
                Debug.Log(
                    $"[{nameof(LensLabLiveCameraBackground)}] Bound live background texture: " +
                    $"{texture.name} ({texture.width}x{texture.height}).",
                    this
                );
            }
        }

        public void SetUseGpuUndistortion(bool enabled)
        {
            if (useGpuUndistortion == enabled)
            {
                return;
            }

            useGpuUndistortion = enabled;
            lastAppliedTexture = null;
            UpdateLiveBackground();
        }

        private Texture ResolveSourceTexture()
        {
            if (poseClient != null && poseClient.HasFrame && poseClient.CurrentTexture != null)
            {
                return poseClient.CurrentTexture;
            }

            return null;
        }

        private Texture RunUndistortion(Texture source)
        {
            if (undistortionController == null)
            {
                if (!warnedAboutMissingUndistortionController)
                {
                    warnedAboutMissingUndistortionController = true;
                    Debug.LogWarning(
                        $"[{nameof(LensLabLiveCameraBackground)}] " +
                        "Use Gpu Undistortion is enabled but no LensLabUndistortionController is in the scene. " +
                        "Falling back to the raw camera frame.",
                        this
                    );
                }
                return source;
            }

            undistortionController.SetInputTexture(source, false);
            if ((runUndistortionEveryCameraFrame && poseClient != null && poseClient.DidUpdateThisFrame)
                || undistortionController.OutputTexture == null)
            {
                undistortionController.RunUndistortion();
            }

            return undistortionController.OutputTexture != null
                ? undistortionController.OutputTexture
                : source;
        }

        private void TryAutoAssignDependencies()
        {
            if (poseClient == null)
            {
                poseClient = GetComponent<LensLabPoseClient>();
            }

            if (poseClient == null)
            {
                poseClient = FindObjectOfType<LensLabPoseClient>(true);
            }

            if (validationOverlay == null)
            {
                validationOverlay = FindObjectOfType<LensLabProjectionValidationOverlay>(true);
            }

            if (undistortionController == null)
            {
                undistortionController = FindObjectOfType<LensLabUndistortionController>(true);
            }

            if (calibrationLoader == null)
            {
                calibrationLoader = FindObjectOfType<LensLabCalibrationLoader>(true);
            }
        }

        private void WarnAboutMissingDependenciesOnce()
        {
            if (warnedAboutMissingDependencies)
            {
                return;
            }

            warnedAboutMissingDependencies = true;
            Debug.LogError(
                $"[{nameof(LensLabLiveCameraBackground)}] Missing setup. " +
                $"PoseClient assigned: {poseClient != null}, " +
                $"ValidationOverlay assigned: {validationOverlay != null}.",
                this
            );
        }

        private void WarnAboutWaitingForFramesOnce()
        {
            if (warnedAboutWaitingForFrames)
            {
                return;
            }

            warnedAboutWaitingForFrames = true;
            Debug.Log(
                $"[{nameof(LensLabLiveCameraBackground)}] Waiting for camera frames. " +
                "Make sure pose_server.py is running.",
                this
            );
        }

        private void WarnAboutResolutionMismatchOnce(Texture source)
        {
            if (!warnIfResolutionDiffersFromCalibration || calibrationLoader == null || source == null)
            {
                return;
            }

            if (!calibrationLoader.HasValidCalibration)
            {
                calibrationLoader.LoadCalibration();
                if (!calibrationLoader.HasValidCalibration)
                {
                    return;
                }
            }

            var size = new Vector2Int(source.width, source.height);
            if (size == lastReportedSize)
            {
                return;
            }

            lastReportedSize = size;
            var calibration = calibrationLoader.LoadedCalibration;
            if (size.x == calibration.image_width && size.y == calibration.image_height)
            {
                if (verboseLogging)
                {
                    Debug.Log(
                        $"[{nameof(LensLabLiveCameraBackground)}] Camera frame size matches calibration: " +
                        $"{size.x}x{size.y}.",
                        this
                    );
                }
                return;
            }

            if (verboseLogging)
            {
                Debug.Log(
                    $"[{nameof(LensLabLiveCameraBackground)}] Frame size {size.x}x{size.y} " +
                    $"does not match calibration {calibration.image_width}x{calibration.image_height}. " +
                    "Python scales intrinsics to the actual frame size; use matching resolutions " +
                    "only for strict validation.",
                    this
                );
            }
        }
    }
}
