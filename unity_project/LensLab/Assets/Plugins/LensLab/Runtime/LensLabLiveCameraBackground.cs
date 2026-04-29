using UnityEngine;

namespace LensLab.Runtime
{
    public class LensLabLiveCameraBackground : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private LensLabWebCamSource webCamSource;
        [SerializeField] private LensLabProjectionValidationOverlay validationOverlay;
        [SerializeField] private LensLabUndistortionController undistortionController;
        [SerializeField] private LensLabCalibrationLoader calibrationLoader;

        [Header("Background Mode")]
        [SerializeField] private bool useGpuUndistortion = false;
        [SerializeField] private bool runUndistortionEveryCameraFrame = true;
        [SerializeField] private bool copyRawCameraToRenderTexture = true;
        [SerializeField] private bool useCanvasBackgroundForRawLiveTest = false;

        [Header("Scene Cleanup")]
        [SerializeField] private bool disableStaticPoseDebugForRawLiveTest = true;

        [Header("Validation")]
        [SerializeField] private bool warnIfResolutionDiffersFromCalibration = true;
        [SerializeField] private bool verboseLogging = true;

        private Texture lastAppliedTexture;
        private RenderTexture rawCameraCopy;
        private Vector2Int lastReportedSize;
        private bool warnedAboutMissingDependencies;
        private bool warnedAboutWaitingForCamera;

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

            if (webCamSource == null || validationOverlay == null)
            {
                WarnAboutMissingDependenciesOnce();
                return;
            }

            if (!webCamSource.IsReady)
            {
                if (!webCamSource.IsPlaying)
                {
                    webCamSource.StartCamera();
                }

                WarnAboutWaitingForCameraOnce();
                return;
            }

            warnedAboutWaitingForCamera = false;
            WarnAboutResolutionMismatchOnce();

            var texture = ResolveBackgroundTexture();
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

        private Texture ResolveBackgroundTexture()
        {
            var cameraTexture = webCamSource.CurrentTexture;
            if (!useGpuUndistortion)
            {
                return copyRawCameraToRenderTexture
                    ? UpdateRawCameraCopy(cameraTexture)
                    : cameraTexture;
            }

            if (undistortionController == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabLiveCameraBackground)}] GPU undistortion is enabled but no undistortion controller is assigned.",
                    this
                );
                return cameraTexture;
            }

            undistortionController.SetInputTexture(cameraTexture, false);
            if (runUndistortionEveryCameraFrame && webCamSource.DidUpdateThisFrame)
            {
                undistortionController.RunUndistortion();
            }
            else if (undistortionController.OutputTexture == null)
            {
                undistortionController.RunUndistortion();
            }

            return undistortionController.OutputTexture != null
                ? undistortionController.OutputTexture
                : cameraTexture;
        }

        private Texture UpdateRawCameraCopy(Texture cameraTexture)
        {
            if (cameraTexture == null)
            {
                return null;
            }

            EnsureRawCameraCopy(cameraTexture.width, cameraTexture.height);
            Graphics.Blit(cameraTexture, rawCameraCopy);

            return rawCameraCopy;
        }

        private void EnsureRawCameraCopy(int width, int height)
        {
            if (rawCameraCopy != null && rawCameraCopy.width == width && rawCameraCopy.height == height)
            {
                return;
            }

            ReleaseRawCameraCopy();
            rawCameraCopy = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "LensLabRawWebCamCopy",
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            rawCameraCopy.Create();
        }

        private void TryAutoAssignDependencies()
        {
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
                        "for the raw live camera test. Re-enable it when live pose data is available.",
                        this
                    );
                }
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
                $"WebCam Source assigned: {webCamSource != null}, Validation Overlay assigned: {validationOverlay != null}. " +
                "Add LensLabWebCamSource to this GameObject and assign the Main Camera's LensLabProjectionValidationOverlay.",
                this
            );
        }

        private void WarnAboutWaitingForCameraOnce()
        {
            if (warnedAboutWaitingForCamera)
            {
                return;
            }

            warnedAboutWaitingForCamera = true;
            Debug.Log(
                $"[{nameof(LensLabLiveCameraBackground)}] Waiting for WebCamTexture to become ready. " +
                "If this stays forever, check camera permission or camera index.",
                this
            );
        }

        private void WarnAboutResolutionMismatchOnce()
        {
            if (!warnIfResolutionDiffersFromCalibration || calibrationLoader == null)
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

            var actualSize = webCamSource.ActualSize;
            if (actualSize == lastReportedSize)
            {
                return;
            }

            lastReportedSize = actualSize;
            var calibration = calibrationLoader.LoadedCalibration;
            if (actualSize.x == calibration.image_width && actualSize.y == calibration.image_height)
            {
                if (verboseLogging)
                {
                    Debug.Log(
                        $"[{nameof(LensLabLiveCameraBackground)}] WebCam resolution matches calibration: " +
                        $"{actualSize.x}x{actualSize.y}.",
                        this
                    );
                }
                return;
            }

            Debug.LogWarning(
                $"[{nameof(LensLabLiveCameraBackground)}] WebCam resolution {actualSize.x}x{actualSize.y} " +
                $"does not match calibration {calibration.image_width}x{calibration.image_height}. " +
                "Use the same resolution for the cleanest projection validation.",
                this
            );
        }

        private void OnDestroy()
        {
            ReleaseRawCameraCopy();
        }

        private void ReleaseRawCameraCopy()
        {
            if (rawCameraCopy == null)
            {
                return;
            }

            if (rawCameraCopy.IsCreated())
            {
                rawCameraCopy.Release();
            }

            Destroy(rawCameraCopy);
            rawCameraCopy = null;
        }
    }
}
