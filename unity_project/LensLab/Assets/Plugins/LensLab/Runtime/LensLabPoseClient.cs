using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LensLab.Runtime
{
    /// <summary>
    /// Connects to the Python pose_server.py over TCP on localhost.
    ///
    /// Architecture (publisher mode): the Python server owns the webcam and
    /// pushes (jpeg_frame, pose_json) tuples to us as fast as it can. We do not
    /// open a WebCamTexture in Unity — that path was retired because Windows
    /// WebCamTexture cannot negotiate MJPG and tops out at a few fps.
    ///
    /// A background thread reads messages and stores the latest one in a
    /// "mailbox" (single-slot, drop-old). The Unity main thread drains the
    /// mailbox in <see cref="Update"/>, decodes the JPEG into a Texture2D, and
    /// publishes the pose. This guarantees we never accumulate latency.
    ///
    /// Wire protocol (server -> us):
    ///   [4B uint32 LE: jpeg_len][jpeg bytes]
    ///   [4B uint32 LE: json_len][json bytes]
    /// </summary>
    public class LensLabPoseClient : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string serverHost = "127.0.0.1";
        [SerializeField] private int serverPort = 5555;
        [Tooltip("Seconds to wait between reconnect attempts after we've already connected once.")]
        [SerializeField] private float reconnectDelay = 1f;
        [Tooltip("Seconds between *initial* connection attempts before the first successful connect. " +
                 "Lower than reconnectDelay so Play-mode startup feels instant once Python finishes booting.")]
        [SerializeField] private float initialConnectRetryInterval = 0.25f;
        [Tooltip("Seconds to suppress connection-error warnings after Start. " +
                 "The Python server takes ~1-2s to boot, so failures during that window are normal.")]
        [SerializeField] private float startupGracePeriod = 4f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;
        [SerializeField] private float frameLogInterval = 2f;

        // ------------------------------------------------------------------
        // Public state
        // ------------------------------------------------------------------

        /// <summary>Whether the background thread currently has an open connection.</summary>
        public bool IsConnected => _connected;

        /// <summary>True once at least one frame has been decoded into <see cref="CurrentTexture"/>.</summary>
        public bool HasFrame => _hasFrame;

        /// <summary>The most recently received camera frame, decoded into a Texture2D.
        /// Stays bound to the same Texture2D instance across frames.</summary>
        public Texture2D CurrentTexture => _frameTexture;

        /// <summary>Convenience cast for callers that expect a generic Texture.</summary>
        public Texture Texture => _frameTexture;

        /// <summary>True for exactly one frame after a new image was decoded.</summary>
        public bool DidUpdateThisFrame { get; private set; }

        /// <summary>Latest pose received from the server. May be null before the first message.</summary>
        public LensLabLivePoseData LatestPose
        {
            get { lock (_poseLock) { return _latestPose; } }
        }

        /// <summary>Pixel size of the last decoded frame, or zero before the first frame.</summary>
        public Vector2Int FrameSize => _frameTexture != null
            ? new Vector2Int(_frameTexture.width, _frameTexture.height)
            : Vector2Int.zero;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        // ---- background thread -> main thread mailbox ----
        // _pendingJpeg / _pendingPoseJson hold the most recently received message;
        // the main thread atomically swaps them out, the network thread overwrites
        // (drop-old). _hasPending acts as a one-bit "new data" flag.
        private readonly object _mailboxLock = new object();
        private byte[] _pendingJpeg;
        private string _pendingPoseJson;
        private volatile bool _hasPending;

        // ---- main-thread-only state ----
        private Texture2D _frameTexture;
        private bool _hasFrame;
        private int _frameCounter;
        private float _lastFrameLogTime;

        // ---- pose, exposed to any thread ----
        private LensLabLivePoseData _latestPose;
        private readonly object _poseLock = new object();

        // ---- network thread ----
        private Thread _networkThread;
        private volatile bool _running;
        private volatile bool _connected;
        private volatile bool _everConnected;
        // Stopwatch is thread-safe; Time.realtimeSinceStartup is main-thread only.
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly object _logLock = new object();
        private string _pendingInfoLog;
        private string _pendingWarningLog;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            _stopwatch.Restart();
            StartNetworkThread();
        }

        private void Update()
        {
            DidUpdateThisFrame = false;
            FlushThreadLogs();

            // Fast path: no new data, nothing to do on the main thread.
            if (!_hasPending)
            {
                return;
            }

            byte[] jpegBytes;
            string poseJson;
            lock (_mailboxLock)
            {
                jpegBytes = _pendingJpeg;
                poseJson = _pendingPoseJson;
                _pendingJpeg = null;
                _pendingPoseJson = null;
                _hasPending = false;
            }

            if (jpegBytes != null && jpegBytes.Length > 0)
            {
                EnsureFrameTexture();
                if (_frameTexture.LoadImage(jpegBytes, markNonReadable: false))
                {
                    _hasFrame = true;
                    DidUpdateThisFrame = true;
                    _frameCounter++;
                }
            }

            LensLabLivePoseData pose = null;
            if (!string.IsNullOrEmpty(poseJson))
            {
                try
                {
                    pose = JsonUtility.FromJson<LensLabLivePoseData>(poseJson);
                }
                catch (Exception)
                {
                    // Bad JSON shouldn't kill the stream; just drop this pose.
                    pose = null;
                }
            }

            if (pose != null)
            {
                lock (_poseLock) { _latestPose = pose; }
            }

            LogFpsPeriodically();
        }

        private void OnDestroy()
        {
            _running = false;
            _networkThread?.Join(500);
            if (_frameTexture != null)
            {
                Destroy(_frameTexture);
                _frameTexture = null;
            }
        }

        // ------------------------------------------------------------------
        // Frame texture management (main thread)
        // ------------------------------------------------------------------

        private void EnsureFrameTexture()
        {
            if (_frameTexture != null)
            {
                return;
            }

            // Texture2D.LoadImage will resize as needed; we just need an instance.
            _frameTexture = new Texture2D(2, 2, TextureFormat.RGB24, false)
            {
                name = "LensLabPoseClientFrame",
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private void LogFpsPeriodically()
        {
            if (!verboseLogging || frameLogInterval <= 0f)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (_lastFrameLogTime == 0f)
            {
                _lastFrameLogTime = now;
                _frameCounter = 0;
                return;
            }

            var dt = now - _lastFrameLogTime;
            if (dt < frameLogInterval)
            {
                return;
            }

            var fps = _frameCounter / dt;
            var size = FrameSize;
            Debug.Log(
                $"[{nameof(LensLabPoseClient)}] {fps:F1} fps  connected={_connected}  " +
                $"frame={size.x}x{size.y}  hasPose={(LatestPose != null)}",
                this
            );
            _lastFrameLogTime = now;
            _frameCounter = 0;
        }

        // ------------------------------------------------------------------
        // Network thread
        // ------------------------------------------------------------------

        private void StartNetworkThread()
        {
            _running = true;
            _networkThread = new Thread(NetworkLoop)
            {
                IsBackground = true,
                Name = "LensLabPoseClient",
            };
            _networkThread.Start();
        }

        private void QueueThreadLog(string message, bool warning)
        {
            lock (_logLock)
            {
                if (warning)
                {
                    _pendingWarningLog = message;
                }
                else
                {
                    _pendingInfoLog = message;
                }
            }
        }

        private void FlushThreadLogs()
        {
            string info;
            string warning;
            lock (_logLock)
            {
                info = _pendingInfoLog;
                warning = _pendingWarningLog;
                _pendingInfoLog = null;
                _pendingWarningLog = null;
            }

            if (!string.IsNullOrEmpty(info))
            {
                Debug.Log(info, this);
            }

            if (!string.IsNullOrEmpty(warning))
            {
                Debug.LogWarning(warning, this);
            }
        }

        private void NetworkLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                NetworkStream stream = null;

                try
                {
                    client = new TcpClient { NoDelay = true };
                    client.Connect(serverHost, serverPort);
                    stream = client.GetStream();
                    _connected = true;
                    _everConnected = true;

                    if (verboseLogging)
                    {
                        QueueThreadLog(
                            $"[{nameof(LensLabPoseClient)}] Connected to pose server " +
                            $"{serverHost}:{serverPort}.",
                            warning: false
                        );
                    }

                    while (_running && client.Connected)
                    {
                        var jpegBytes = ReadLengthPrefixed(stream, maxLength: 20 * 1024 * 1024);
                        var jsonBytes = ReadLengthPrefixed(stream, maxLength: 1 * 1024 * 1024);
                        var json = System.Text.Encoding.UTF8.GetString(jsonBytes);

                        // Drop-old: overwrite the mailbox unconditionally.
                        lock (_mailboxLock)
                        {
                            _pendingJpeg = jpegBytes;
                            _pendingPoseJson = json;
                            _hasPending = true;
                        }
                    }
                }
                catch (Exception ex) when (!_running)
                {
                    _ = ex;
                }
                catch (Exception ex)
                {
                    _connected = false;

                    // Suppress noisy warnings during the grace period: the Python
                    // server typically takes 1-2s to boot, so initial "connection
                    // refused" errors are expected, not real problems.
                    var inGrace = !_everConnected
                                  && _stopwatch.Elapsed.TotalSeconds < startupGracePeriod;
                    if (verboseLogging && !inGrace)
                    {
                        QueueThreadLog(
                            $"[{nameof(LensLabPoseClient)}] Connection error: {ex.Message}. " +
                            $"Reconnecting...",
                            warning: true
                        );
                    }
                }
                finally
                {
                    _connected = false;
                    stream?.Close();
                    client?.Close();
                }

                if (_running)
                {
                    // Initial connect: poll fast so we latch on the moment Python is ready.
                    // After we've connected at least once: back off so reconnect storms are quiet.
                    var delay = _everConnected ? reconnectDelay : initialConnectRetryInterval;
                    Thread.Sleep(Math.Max(50, (int)Math.Round(delay * 1000f)));
                }
            }
        }

        // ------------------------------------------------------------------
        // Protocol helpers (background thread)
        // ------------------------------------------------------------------

        private static byte[] ReadLengthPrefixed(NetworkStream stream, int maxLength)
        {
            var header = ReadExact(stream, 4);
            var length = BitConverter.IsLittleEndian
                ? BitConverter.ToUInt32(header, 0)
                : SwapUInt32(BitConverter.ToUInt32(header, 0));

            if (length == 0 || length > maxLength)
            {
                throw new InvalidDataException($"Implausible payload length: {length}");
            }

            return ReadExact(stream, (int)length);
        }

        private static uint SwapUInt32(uint v)
        {
            return ((v & 0x000000FFu) << 24)
                 | ((v & 0x0000FF00u) << 8)
                 | ((v & 0x00FF0000u) >> 8)
                 | ((v & 0xFF000000u) >> 24);
        }

        private static byte[] ReadExact(NetworkStream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("Connection closed while reading.");
                }
                offset += read;
            }
            return buffer;
        }
    }
}
