using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Connects to the Python pose_server.py over TCP on localhost.
    ///
    /// Each frame (or at a configurable interval), the current WebCamTexture is
    /// JPEG-encoded on the main thread and queued for the background network thread.
    /// The background thread sends the JPEG to Python, receives the JSON pose
    /// response, and stores it thread-safely in <see cref="LatestPose"/>.
    ///
    /// Protocol (both directions): 4-byte little-endian uint32 length, then payload.
    /// </summary>
    public class LensLabPoseClient : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string serverHost = "127.0.0.1";
        [SerializeField] private int serverPort = 5555;
        [SerializeField] private float reconnectDelay = 2f;

        [Header("Capture")]
        [SerializeField] private LensLabWebCamSource webCamSource;
        [SerializeField] [Range(10, 95)] private int jpegQuality = 75;
        [Tooltip("Minimum seconds between frames sent to the server. 0.033 ≈ 30 fps.")]
        [SerializeField] private float sendInterval = 0.033f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;

        // ------------------------------------------------------------------
        // Public state
        // ------------------------------------------------------------------

        /// <summary>Whether the background thread currently has an open connection.</summary>
        public bool IsConnected => _connected;

        /// <summary>
        /// Latest pose received from the server. Updated from the background thread;
        /// safe to read from any thread. May be null before the first response arrives.
        /// </summary>
        public LensLabLivePoseData LatestPose
        {
            get { lock (_poseLock) { return _latestPose; } }
            private set { lock (_poseLock) { _latestPose = value; } }
        }

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private LensLabLivePoseData _latestPose;
        private readonly object _poseLock = new object();

        // One pending JPEG at a time; background thread drains it.
        private readonly ConcurrentQueue<byte[]> _frameQueue = new ConcurrentQueue<byte[]>();

        private Thread _networkThread;
        private volatile bool _running;
        private volatile bool _connected;

        private Texture2D _captureBuffer;
        private float _lastSendTime;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            TryAutoAssignDependencies();
            StartNetworkThread();
        }

        private void Update()
        {
            if (webCamSource == null || !webCamSource.IsReady)
            {
                return;
            }

            // Only send when the camera has delivered a new frame AND the
            // send interval has elapsed AND the queue is drained.
            if (!webCamSource.DidUpdateThisFrame)
            {
                return;
            }

            if (Time.realtimeSinceStartup - _lastSendTime < sendInterval)
            {
                return;
            }

            if (_frameQueue.Count > 0)
            {
                return;
            }

            _lastSendTime = Time.realtimeSinceStartup;
            EnqueueCurrentFrame();
        }

        private void OnDestroy()
        {
            _running = false;
            _networkThread?.Join(500);
            if (_captureBuffer != null)
            {
                Destroy(_captureBuffer);
            }
        }

        // ------------------------------------------------------------------
        // Frame capture (main thread)
        // ------------------------------------------------------------------

        private void EnqueueCurrentFrame()
        {
            var webcamTex = webCamSource.Texture;
            if (webcamTex == null)
            {
                return;
            }

            EnsureCaptureBuffer(webcamTex.width, webcamTex.height);

            // GetPixels32 must be called on the main thread.
            _captureBuffer.SetPixels32(webcamTex.GetPixels32());
            _captureBuffer.Apply(false);

            var jpegBytes = _captureBuffer.EncodeToJPG(jpegQuality);
            if (jpegBytes != null && jpegBytes.Length > 0)
            {
                _frameQueue.Enqueue(jpegBytes);
            }
        }

        private void EnsureCaptureBuffer(int width, int height)
        {
            if (_captureBuffer != null
                && _captureBuffer.width == width
                && _captureBuffer.height == height)
            {
                return;
            }

            if (_captureBuffer != null)
            {
                Destroy(_captureBuffer);
            }

            _captureBuffer = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
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

        private void NetworkLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                NetworkStream stream = null;

                try
                {
                    client = new TcpClient();
                    client.Connect(serverHost, serverPort);
                    stream = client.GetStream();
                    _connected = true;

                    if (verboseLogging)
                    {
                        Debug.Log(
                            $"[{nameof(LensLabPoseClient)}] Connected to pose server " +
                            $"{serverHost}:{serverPort}.",
                            this
                        );
                    }

                    while (_running && client.Connected)
                    {
                        if (!_frameQueue.TryDequeue(out var jpegBytes))
                        {
                            Thread.Sleep(5);
                            continue;
                        }

                        SendFrame(stream, jpegBytes);
                        var response = ReceiveResponse(stream);
                        if (response != null)
                        {
                            LatestPose = response;
                        }
                    }
                }
                catch (Exception ex) when (!_running)
                {
                    // Shutting down; swallow the exception.
                    _ = ex;
                }
                catch (Exception ex)
                {
                    _connected = false;
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[{nameof(LensLabPoseClient)}] Connection error: {ex.Message}. " +
                            $"Reconnecting in {reconnectDelay}s.",
                            this
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
                    Thread.Sleep(Mathf.Max(100, Mathf.RoundToInt(reconnectDelay * 1000)));
                }
            }
        }

        // ------------------------------------------------------------------
        // Protocol helpers (background thread)
        // ------------------------------------------------------------------

        private static void SendFrame(NetworkStream stream, byte[] jpegBytes)
        {
            // 4-byte little-endian length header + JPEG payload
            var header = BitConverter.GetBytes((uint)jpegBytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }

            stream.Write(header, 0, 4);
            stream.Write(jpegBytes, 0, jpegBytes.Length);
        }

        private static LensLabLivePoseData ReceiveResponse(NetworkStream stream)
        {
            var header = ReadExact(stream, 4);
            var responseLength = BitConverter.ToUInt32(header, 0);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
                responseLength = BitConverter.ToUInt32(header, 0);
            }

            if (responseLength == 0 || responseLength > 1024 * 1024)
            {
                throw new InvalidDataException($"Implausible response length: {responseLength}");
            }

            var responseBytes = ReadExact(stream, (int)responseLength);
            var json = System.Text.Encoding.UTF8.GetString(responseBytes);
            return JsonUtility.FromJson<LensLabLivePoseData>(json);
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

        // ------------------------------------------------------------------
        // Auto-wiring
        // ------------------------------------------------------------------

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
        }
    }
}
