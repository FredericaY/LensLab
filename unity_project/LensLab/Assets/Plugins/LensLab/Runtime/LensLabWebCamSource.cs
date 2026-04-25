using UnityEngine;

namespace LensLab.Runtime
{
    public class LensLabWebCamSource : MonoBehaviour
    {
        [Header("Device")]
        [SerializeField] private int cameraIndex = 0;
        [SerializeField] private string preferredDeviceName = "";

        [Header("Capture")]
        [SerializeField] private int requestedWidth = 1920;
        [SerializeField] private int requestedHeight = 1080;
        [SerializeField] private int requestedFps = 30;

        [Header("Behavior")]
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private bool stopOnDisable = true;
        [SerializeField] private bool verboseLogging = true;
        [SerializeField] private float frameStatusLogInterval = 2f;

        private WebCamTexture webCamTexture;
        private string activeDeviceName;
        private Vector2Int lastReportedSize;
        private int updatedFrameCount;
        private float lastFrameStatusLogTime;

        public WebCamTexture Texture => webCamTexture;
        public Texture CurrentTexture => webCamTexture;
        public bool IsPlaying => webCamTexture != null && webCamTexture.isPlaying;
        public bool IsReady => IsPlaying && webCamTexture.width > 16 && webCamTexture.height > 16;
        public bool DidUpdateThisFrame => webCamTexture != null && webCamTexture.didUpdateThisFrame;
        public int UpdatedFrameCount => updatedFrameCount;
        public Vector2Int RequestedSize => new Vector2Int(requestedWidth, requestedHeight);
        public Vector2Int ActualSize => webCamTexture != null
            ? new Vector2Int(webCamTexture.width, webCamTexture.height)
            : Vector2Int.zero;
        public string ActiveDeviceName => activeDeviceName;
        public int VideoRotationAngle => webCamTexture != null ? webCamTexture.videoRotationAngle : 0;
        public bool VideoVerticallyMirrored => webCamTexture != null && webCamTexture.videoVerticallyMirrored;

        private void Start()
        {
            if (playOnStart)
            {
                StartCamera();
            }
        }

        private void Update()
        {
            if (DidUpdateThisFrame)
            {
                updatedFrameCount++;
            }

            if (!IsReady)
            {
                return;
            }

            var actualSize = ActualSize;
            if (actualSize == lastReportedSize)
            {
                LogFrameStatusPeriodically();
                return;
            }

            lastReportedSize = actualSize;
            if (verboseLogging)
            {
                Debug.Log(BuildDebugSummary(), this);
            }

            LogFrameStatusPeriodically();
        }

        [ContextMenu("Start Camera")]
        public void StartCamera()
        {
            if (IsPlaying)
            {
                return;
            }

            var deviceName = ResolveDeviceName();
            if (string.IsNullOrEmpty(deviceName))
            {
                Debug.LogError($"[{nameof(LensLabWebCamSource)}] No WebCam device is available.", this);
                return;
            }

            activeDeviceName = deviceName;
            webCamTexture = new WebCamTexture(
                activeDeviceName,
                Mathf.Max(1, requestedWidth),
                Mathf.Max(1, requestedHeight),
                Mathf.Max(1, requestedFps)
            )
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            webCamTexture.Play();
            lastReportedSize = Vector2Int.zero;
            updatedFrameCount = 0;
            lastFrameStatusLogTime = Time.realtimeSinceStartup;

            if (verboseLogging)
            {
                Debug.Log(
                    $"[{nameof(LensLabWebCamSource)}] Started WebCam '{activeDeviceName}' " +
                    $"with requested size {requestedWidth}x{requestedHeight}@{requestedFps}.",
                    this
                );
            }
        }

        [ContextMenu("Stop Camera")]
        public void StopCamera()
        {
            if (webCamTexture == null)
            {
                return;
            }

            if (webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }

            Destroy(webCamTexture);
            webCamTexture = null;
            activeDeviceName = "";
            lastReportedSize = Vector2Int.zero;
            updatedFrameCount = 0;
        }

        [ContextMenu("Restart Camera")]
        public void RestartCamera()
        {
            StopCamera();
            StartCamera();
        }

        private string ResolveDeviceName()
        {
            var devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (var i = 0; i < devices.Length; i++)
                {
                    if (devices[i].name == preferredDeviceName)
                    {
                        return devices[i].name;
                    }
                }

                Debug.LogWarning(
                    $"[{nameof(LensLabWebCamSource)}] Preferred device '{preferredDeviceName}' was not found. " +
                    "Falling back to camera index.",
                    this
                );
            }

            var clampedIndex = Mathf.Clamp(cameraIndex, 0, devices.Length - 1);
            return devices[clampedIndex].name;
        }

        public string BuildDebugSummary()
        {
            var actual = ActualSize;
            return
                $"[{nameof(LensLabWebCamSource)}] WebCam ready.\n" +
                $"Device: {activeDeviceName}\n" +
                $"Requested: {requestedWidth}x{requestedHeight}@{requestedFps}\n" +
                $"Actual: {actual.x}x{actual.y}\n" +
                $"Updated Frames: {updatedFrameCount}\n" +
                $"Rotation: {VideoRotationAngle} degrees, Vertically Mirrored: {VideoVerticallyMirrored}";
        }

        private void LogFrameStatusPeriodically()
        {
            if (!verboseLogging || frameStatusLogInterval <= 0f)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now - lastFrameStatusLogTime < frameStatusLogInterval)
            {
                return;
            }

            lastFrameStatusLogTime = now;
            Debug.Log(
                $"[{nameof(LensLabWebCamSource)}] Frame status: " +
                $"playing={IsPlaying}, ready={IsReady}, didUpdateThisFrame={DidUpdateThisFrame}, " +
                $"updatedFrames={updatedFrameCount}, actual={ActualSize.x}x{ActualSize.y}.",
                this
            );
        }

        private void OnDisable()
        {
            if (stopOnDisable)
            {
                StopCamera();
            }
        }

        private void OnDestroy()
        {
            StopCamera();
        }
    }
}
