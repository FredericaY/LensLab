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

        [Header("Python / Conda")]
        [Tooltip(
            "Leave empty to call Python directly (uses Python Executable below).\n" +
            "Set to your conda environment name (e.g. 'lenslab') to launch via\n" +
            "'conda run --no-capture-output -n <name> python ...'\n" +
            "This is the recommended setting when packages are installed in a conda env."
        )]
        [SerializeField] private string condaEnvironmentName = "lenslab";

        [Tooltip(
            "Python executable used when Conda Environment Name is empty.\n" +
            "Use 'python', 'python3', or a full path such as\n" +
            "C:/Users/you/miniconda3/envs/lenslab/python.exe"
        )]
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

            var (executable, arguments) = BuildCommand(scriptPath);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
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
                var hint = string.IsNullOrEmpty(condaEnvironmentName.Trim())
                    ? $"Make sure '{pythonExecutable}' is on your PATH."
                    : $"Make sure 'conda' is on your PATH and the environment '{condaEnvironmentName.Trim()}' exists.";
                UnityEngine.Debug.LogError(
                    $"[{nameof(LensLabPoseServerLauncher)}] Failed to start pose server: {ex.Message}\n" +
                    $"{hint}\nScript path: {scriptPath}",
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

        /// <summary>
        /// Returns (executableFileName, arguments) for the process.
        /// When a conda environment is specified the command becomes:
        ///   conda run --no-capture-output -n &lt;env&gt; python "&lt;script&gt;" [args]
        /// Otherwise:
        ///   &lt;pythonExecutable&gt; "&lt;script&gt;" [args]
        /// </summary>
        private (string executable, string arguments) BuildCommand(string scriptPath)
        {
            var serverArgs = $"--port {port} --min-corners {minCorners}";
            if (!verboseServer)
            {
                serverArgs += " --no-verbose";
            }

            var envName = condaEnvironmentName.Trim();
            if (!string.IsNullOrEmpty(envName))
            {
                // conda run --no-capture-output ensures stdout/stderr are streamed
                // in real time rather than buffered until the process exits.
                var arguments = $"run --no-capture-output -n \"{envName}\" python \"{scriptPath}\" {serverArgs}";
                return ("conda", arguments);
            }

            return (pythonExecutable, $"\"{scriptPath}\" {serverArgs}");
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
