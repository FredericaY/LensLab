using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Binds a live camera-feed Texture to the projection-validation overlay.
    ///
    /// Frame source priority:
    ///   1. LensLabPoseClient (publisher mode — Python owns the camera).
    ///   2. LensLabWebCamSource (legacy mode — Unity owns the camera). Kept for
    ///      offline tests; not used in the default scene.
    ///
    /// In publisher mode this script does no per-frame GPU work: the PoseClient
    /// already produces a Texture2D each frame, and we just rebind it to the
    /// overlay when its identity changes.
    /// </summary>
    public class LensLabLiveCameraBackground : MonoBehaviour
    {
        [Header("Frame source (preferred: PoseClient)")]
        [SerializeField] private LensLabPoseClient poseClient;
        [SerializeField] private LensLabWebCamSource webCamSource;
        [SerializeField] private LensLabProjectionValidationOverlay validationOverlay;
        [SerializeField] private LensLabUndistortionController undistortionController;
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;

        [Header("Background Mode")]
        [SerializeField] private bool useGpuUndistortion = false;
        [SerializeField] private bool runUndistortionEveryCameraFrame = true;
        [SerializeField] private bool useCanvasBackgroundForRawLiveTest = false;

        [Header("Scene Cleanup")]
        [SerializeField] private bool disableStaticPoseDebugForRawLiveTest = true;

        [Header("Validation")]
        [SerializeField] private bool warnIfResolutionDiffersFromCalibration = true;
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
            DisableStaticPoseDebugIfRequested();
        }

        private void LateUpdate()
        {
            UpdateLiveBackground();
        }

        [ContextMenu("Update Live Background")]
        public void UpdateLiveBackground()
        {
            TryAutoAssignDependencies();

            if (validationOverlay == null || (poseClient == null && webCamSource == null))
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

            if (texture == null)
            {
                return;
            }

            if (texture != lastAppliedTexture)
            {
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

        // ------------------------------------------------------------------
        // Frame source resolution
        // ------------------------------------------------------------------

        private Texture ResolveSourceTexture()
        {
            // Prefer the network-driven pose client (publisher mode).
            if (poseClient != null && poseClient.HasFrame && poseClient.CurrentTexture != null)
            {
                return poseClient.CurrentTexture;
            }

            // Fall back to a locally-owned WebCamTexture (legacy / offline test).
            if (webCamSource != null)
            {
                if (!webCamSource.IsReady)
                {
                    if (!webCamSource.IsPlaying)
                    {
                        webCamSource.StartCamera();
                    }
                    return null;
                }
                return webCamSource.CurrentTexture;
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
                        "Falling back to the raw camera frame for the background. " +
                        "Disable 'Use Gpu Undistortion' in the inspector to silence this warning.",
                        this
                    );
                }
                return source;
            }

            undistortionController.SetInputTexture(source, false);
            if (runUndistortionEveryCameraFrame && DidSourceUpdateThisFrame())
            {
                undistortionController.RunUndistortion();
            }
            else if (undistortionController.OutputTexture == null)
            {
                undistortionController.RunUndistortion();
            }

            return undistortionController.OutputTexture != null
                ? undistortionController.OutputTexture
                : source;
        }

        private bool DidSourceUpdateThisFrame()
        {
            if (poseClient != null && poseClient.HasFrame)
            {
                return poseClient.DidUpdateThisFrame;
            }
            return webCamSource != null && webCamSource.DidUpdateThisFrame;
        }

        // ------------------------------------------------------------------
        // Dependency wiring
        // ------------------------------------------------------------------

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

            if (webCamSource == null)
            {
                webCamSource = GetComponent<LensLabWebCamSource>();
            }
            if (webCamSource == null)
            {
                webCamSource = FindObjectOfType<LensLabWebCamSource>(true);
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

        private void DisableStaticPoseDebugIfRequested()
        {
            if (!disableStaticPoseDebugForRawLiveTest)
            {
                return;
            }

            var poseDebug = FindObjectOfType<LensLabPoseBoardDebug>(true);
            if (poseDebug != null && poseDebug.enabled)
            {
                poseDebug.enabled = false;
                if (verboseLogging)
                {
                    Debug.Log(
                        $"[{nameof(LensLabLiveCameraBackground)}] Disabled {nameof(LensLabPoseBoardDebug)} " +
                        "for the live camera test. Re-enable it when static pose data should be displayed.",
                        this
                    );
                }
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        private void WarnAboutMissingDependenciesOnce()
        {
            if (warnedAboutMissingDependencies)
            {
                return;
            }

            warnedAboutMissingDependencies = true;
            Debug.LogError(
                $"[{nameof(LensLabLiveCameraBackground)}] Missing setup. " +
                $"PoseClient assigned: {poseClient != null}, WebCamSource assigned: {webCamSource != null}, " +
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

            Debug.LogWarning(
                $"[{nameof(LensLabLiveCameraBackground)}] Frame size {size.x}x{size.y} " +
                $"does not match calibration {calibration.image_width}x{calibration.image_height}. " +
                "Use the same resolution for the cleanest projection validation.",
                this
            );
        }
    }
}
