using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Launches and manages the Python pose_server.py process from within Unity.
    ///
    /// When <see cref="launchOnPlay"/> is enabled (default), pressing Play in the
    /// Editor automatically starts the server and stops it when Play ends. The
    /// server's stdout and stderr are forwarded to the Unity Console so you do not
    /// need a separate terminal.
    ///
    /// Path resolution
    /// ---------------
    /// With <see cref="autoResolvePath"/> enabled the launcher walks three directories
    /// above <c>Application.dataPath</c> to find the repository root, then appends
    /// <c>calibration/scripts/pose_server.py</c>. This matches the LensLab repository
    /// layout. Override with <see cref="customScriptPath"/> if needed.
    ///
    /// Standalone builds
    /// -----------------
    /// The launcher silently skips startup if the script file cannot be found, so it
    /// does not break standalone builds. The Python server is a development-time tool.
    /// </summary>
    public class LensLabPoseServerLauncher : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector
        // ------------------------------------------------------------------

        [Header("Startup")]
        [Tooltip("Automatically start the server when entering Play mode.")]
        [SerializeField] private bool launchOnPlay = true;

        [Header("Python")]
        [Tooltip("Python executable name or full path. 'python' or 'python3'.")]
        [SerializeField] private string pythonExecutable = "python";

        [Header("Server Arguments")]
        [Tooltip("TCP port the server listens on. Must match LensLabPoseClient.serverPort.")]
        [SerializeField] private int port = 5555;
        [Tooltip("Minimum ChArUco corners required for a valid pose. Passed as --min-corners.")]
        [SerializeField] private int minCorners = 4;
        [Tooltip("Print per-frame detection results in the Python console (forwarded to Unity Console).")]
        [SerializeField] private bool verboseServer = true;

        [Header("Script Path")]
        [Tooltip("Resolve pose_server.py automatically from the repository root (recommended).")]
        [SerializeField] private bool autoResolvePath = true;
        [Tooltip("Used only when Auto Resolve Path is disabled. Absolute or relative-to-Assets path.")]
        [SerializeField] private string customScriptPath = "";

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = true;

        // ------------------------------------------------------------------
        // Public state
        // ------------------------------------------------------------------

        /// <summary>True while the Python process is alive.</summary>
        public bool IsRunning => _process != null && !_process.HasExited;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private Process _process;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            if (launchOnPlay)
            {
                StartServer();
            }
        }

        private void OnDestroy()
        {
            StopServer();
        }

        private void OnApplicationQuit()
        {
            StopServer();
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Start the Python pose server. Safe to call multiple times.</summary>
        [ContextMenu("Start Server")]
        public void StartServer()
        {
            if (IsRunning)
            {
                if (verboseLogging)
                {
                    UnityEngine.Debug.Log($"[{nameof(LensLabPoseServerLauncher)}] Server is already running (PID {_process.Id}).", this);
                }
                return;
            }

            var scriptPath = ResolveScriptPath();
            if (string.IsNullOrEmpty(scriptPath))
            {
                return;
            }

            var arguments = BuildArguments(scriptPath);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonExecutable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnServerOutput;
                _process.ErrorDataReceived += OnServerError;
                _process.Exited += OnServerExited;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                if (verboseLogging)
                {
                    UnityEngine.Debug.Log(
                        $"[{nameof(LensLabPoseServerLauncher)}] Started pose server " +
                        $"(PID {_process.Id}) on port {port}.\n" +
                        $"Command: {pythonExecutable} {arguments}",
                        this
                    );
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(
                    $"[{nameof(LensLabPoseServerLauncher)}] Failed to start pose server: {ex.Message}\n" +
                    $"Make sure '{pythonExecutable}' is on your PATH and the script exists at:\n{scriptPath}",
                    this
                );
                _process = null;
            }
        }

        /// <summary>Stop the Python pose server. Safe to call when not running.</summary>
        [ContextMenu("Stop Server")]
        public void StopServer()
        {
            if (_process == null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);

                    if (verboseLogging)
                    {
                        UnityEngine.Debug.Log($"[{nameof(LensLabPoseServerLauncher)}] Pose server stopped.", this);
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[{nameof(LensLabPoseServerLauncher)}] Exception while stopping server: {ex.Message}",
                    this
                );
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        // ------------------------------------------------------------------
        // Path resolution
        // ------------------------------------------------------------------

        private string ResolveScriptPath()
        {
            if (!autoResolvePath)
            {
                var custom = customScriptPath.Trim();
                if (string.IsNullOrEmpty(custom))
                {
                    UnityEngine.Debug.LogError(
                        $"[{nameof(LensLabPoseServerLauncher)}] Auto Resolve Path is disabled but Custom Script Path is empty.",
                        this
                    );
                    return null;
                }

                if (!Path.IsPathRooted(custom))
                {
                    custom = Path.GetFullPath(Path.Combine(Application.dataPath, custom));
                }

                if (!File.Exists(custom))
                {
                    UnityEngine.Debug.LogError(
                        $"[{nameof(LensLabPoseServerLauncher)}] Script not found at custom path: {custom}",
                        this
                    );
                    return null;
                }

                return custom;
            }

            // Auto-resolve: walk 3 levels up from Assets/ to reach the repo root.
            // Application.dataPath = .../unity_project/LensLab/Assets
            // Repo root            = .../   (3 levels up)
            var repoRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "..", "..")
            );
            var resolved = Path.GetFullPath(
                Path.Combine(repoRoot, "calibration", "scripts", "pose_server.py")
            );

            if (!File.Exists(resolved))
            {
                UnityEngine.Debug.LogWarning(
                    $"[{nameof(LensLabPoseServerLauncher)}] pose_server.py not found at auto-resolved path:\n{resolved}\n" +
                    "Disable Auto Resolve Path and set a Custom Script Path, or run the Python server manually.",
                    this
                );
                return null;
            }

            return resolved;
        }

        private string BuildArguments(string scriptPath)
        {
            var args = $"\"{scriptPath}\" --port {port} --min-corners {minCorners}";
            if (!verboseServer)
            {
                args += " --no-verbose";
            }
            return args;
        }

        // ------------------------------------------------------------------
        // Process event handlers (called on background threads)
        // ------------------------------------------------------------------

        private void OnServerOutput(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                UnityEngine.Debug.Log($"[PoseServer] {e.Data}");
            }
        }

        private void OnServerError(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                UnityEngine.Debug.LogWarning($"[PoseServer] {e.Data}");
            }
        }

        private void OnServerExited(object sender, EventArgs e)
        {
            var exitCode = _process?.ExitCode ?? -1;
            if (verboseLogging)
            {
                UnityEngine.Debug.Log(
                    $"[{nameof(LensLabPoseServerLauncher)}] Server process exited (code {exitCode})."
                );
            }
        }
    }
}
