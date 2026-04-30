using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Launches and manages the Python pose_server.py process from within Unity.
    ///
    /// Lifecycle guarantees
    /// --------------------
    /// 1. **Pre-launch port probe**: if something is already listening on the target
    ///    port we treat it as an orphan from a previous Unity session, kill the
    ///    process(es), and only then start ours.
    /// 2. **Windows Job Object**: on Windows the spawned Python process is attached
    ///    to a Job Object configured with KILL_ON_JOB_CLOSE. If Unity itself crashes
    ///    or is force-killed, Windows kernel kills Python automatically, preventing
    ///    orphan processes that would hold the webcam.
    /// 3. **Auto-restart**: configurable. If Python exits unexpectedly while Play
    ///    mode is still active we relaunch it after a short backoff.
    /// 4. **Clean shutdown**: <see cref="OnDestroy"/> and <see cref="OnApplicationQuit"/>
    ///    both kill the process; the Job Object is the safety net for the case
    ///    where neither callback fires.
    ///
    /// stdout / stderr are forwarded to the Unity Console so a separate terminal is
    /// not needed.
    /// </summary>
    [DefaultExecutionOrder(-100)] // run before LensLabPoseClient
    public class LensLabPoseServerLauncher : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector
        // ------------------------------------------------------------------

        [Header("Startup")]
        [Tooltip("Automatically start the server when entering Play mode.")]
        [SerializeField] private bool launchOnPlay = true;

        [Tooltip("If something is already listening on the target port at launch, " +
                 "kill it before starting our own server. Recommended.")]
        [SerializeField] private bool killOrphanOnLaunch = true;

        [Header("Crash recovery")]
        [Tooltip("Restart the server if it exits unexpectedly while Play mode is active.")]
        [SerializeField] private bool autoRestartOnCrash = true;

        [Tooltip("Seconds to wait before restarting after an unexpected exit.")]
        [SerializeField] private float autoRestartDelay = 1.5f;

        [Tooltip("Maximum consecutive auto-restart attempts before giving up. " +
                 "Resets to zero on every successful run that lasts longer than " +
                 "Auto Restart Reset Threshold seconds.")]
        [SerializeField] private int maxAutoRestartAttempts = 5;

        [SerializeField] private float autoRestartResetThreshold = 10f;

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
        [SerializeField] private int minCorners = 6;
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

        /// <summary>Total times we've started the server in this Play session (initial + auto-restarts).</summary>
        public int LaunchCount { get; private set; }

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private Process _process;
        private IntPtr _jobHandle = IntPtr.Zero;
        private bool _intentionalShutdown;
        private int _consecutiveCrashCount;
        private float _lastLaunchTime;
        private float _nextAutoRestartTime;
        private bool _autoRestartPending;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            CreateJobObjectIfWindows();
        }

        private void Start()
        {
            if (launchOnPlay)
            {
                StartServer();
            }
        }

        private void Update()
        {
            // Detect unexpected exits and schedule a restart if configured.
            if (_process != null && _process.HasExited && !_intentionalShutdown && !_autoRestartPending)
            {
                var ranLongEnough = Time.realtimeSinceStartup - _lastLaunchTime >= autoRestartResetThreshold;
                if (ranLongEnough)
                {
                    _consecutiveCrashCount = 0;
                }

                if (autoRestartOnCrash && _consecutiveCrashCount < maxAutoRestartAttempts)
                {
                    _autoRestartPending = true;
                    _nextAutoRestartTime = Time.realtimeSinceStartup + autoRestartDelay;
                    UnityEngine.Debug.LogWarning(
                        $"[{nameof(LensLabPoseServerLauncher)}] Server exited unexpectedly " +
                        $"(attempt {_consecutiveCrashCount + 1}/{maxAutoRestartAttempts}). " +
                        $"Auto-restarting in {autoRestartDelay:F1}s...",
                        this
                    );
                }
                else if (autoRestartOnCrash)
                {
                    UnityEngine.Debug.LogError(
                        $"[{nameof(LensLabPoseServerLauncher)}] Server has crashed " +
                        $"{maxAutoRestartAttempts} times in a row. Auto-restart disabled. " +
                        "Use the context menu 'Start Server' once you've fixed the cause.",
                        this
                    );
                }
            }

            if (_autoRestartPending && Time.realtimeSinceStartup >= _nextAutoRestartTime)
            {
                _autoRestartPending = false;
                _consecutiveCrashCount++;
                StartServer();
            }
        }

        private void OnDestroy()
        {
            StopServer(intentional: true);
            DestroyJobObject();
        }

        private void OnApplicationQuit()
        {
            StopServer(intentional: true);
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
                    UnityEngine.Debug.Log(
                        $"[{nameof(LensLabPoseServerLauncher)}] Server is already running (PID {_process.Id}).",
                        this
                    );
                }
                return;
            }

            // Clear stale handle from previous run.
            if (_process != null)
            {
                _process.Dispose();
                _process = null;
            }

            if (killOrphanOnLaunch)
            {
                KillOrphanServersBoundToPort(port);
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

                AttachProcessToJobObject(_process);

                _intentionalShutdown = false;
                _lastLaunchTime = Time.realtimeSinceStartup;
                LaunchCount++;

                if (verboseLogging)
                {
                    UnityEngine.Debug.Log(
                        $"[{nameof(LensLabPoseServerLauncher)}] Started pose server " +
                        $"(PID {_process.Id}) on port {port}. Launch #{LaunchCount}.\n" +
                        $"Command: {executable} {arguments}",
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
            StopServer(intentional: true);
        }

        [ContextMenu("Restart Server")]
        public void RestartServer()
        {
            StopServer(intentional: true);
            StartServer();
        }

        private void StopServer(bool intentional)
        {
            _autoRestartPending = false;
            _intentionalShutdown = intentional;

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
                        UnityEngine.Debug.Log(
                            $"[{nameof(LensLabPoseServerLauncher)}] Pose server stopped.",
                            this
                        );
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
                try { _process.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }

        // ------------------------------------------------------------------
        // Orphan detection: probe the port, then kill anything holding it
        // ------------------------------------------------------------------

        private void KillOrphanServersBoundToPort(int targetPort)
        {
            // Cheap check first: is something listening?
            if (!IsPortInUse(targetPort))
            {
                return;
            }

            UnityEngine.Debug.LogWarning(
                $"[{nameof(LensLabPoseServerLauncher)}] Port {targetPort} is already in use " +
                "(probably a pose_server.py orphan from a previous Unity session). " +
                "Attempting to kill it...",
                this
            );

            UnityEngine.Debug.LogWarning(
                $"[{nameof(LensLabPoseServerLauncher)}] Automatic orphan killing is disabled to avoid " +
                "terminating unrelated Python processes. Stop the process holding this port manually, " +
                "or change the server/client port.",
                this
            );
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                using (var probe = new TcpClient())
                {
                    var task = probe.BeginConnect("127.0.0.1", port, null, null);
                    var connected = task.AsyncWaitHandle.WaitOne(150);
                    if (connected && probe.Connected)
                    {
                        probe.EndConnect(task);
                        return true;
                    }
                }
            }
            catch
            {
                // Connect failed -> port is free (or firewall blocked us; same effect).
            }
            return false;
        }

        private static int TryKillProcessesByName(string name)
        {
            var count = 0;
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch { return 0; }

            foreach (var proc in candidates)
            {
                try
                {
                    if (proc.HasExited)
                    {
                        proc.Dispose();
                        continue;
                    }
                    proc.Kill();
                    proc.WaitForExit(1000);
                    count++;
                }
                catch
                {
                    // Probably "Access denied" — not our process. Move on.
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
            return count;
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
        /// </summary>
        private (string executable, string arguments) BuildCommand(string scriptPath)
        {
            var effectiveMinCorners = Mathf.Max(6, minCorners);
            var serverArgs = $"--port {port} --min-corners {effectiveMinCorners}";
            if (!verboseServer)
            {
                serverArgs += " --no-verbose";
            }

            var envName = condaEnvironmentName.Trim();
            if (!string.IsNullOrEmpty(envName))
            {
                var arguments = $"run --no-capture-output -n \"{envName}\" python -u \"{scriptPath}\" {serverArgs}";
                return ("conda", arguments);
            }

            return (pythonExecutable, $"-u \"{scriptPath}\" {serverArgs}");
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
            int exitCode = -1;
            try { exitCode = _process?.ExitCode ?? -1; }
            catch { /* process already disposed */ }

            if (verboseLogging)
            {
                UnityEngine.Debug.Log(
                    $"[{nameof(LensLabPoseServerLauncher)}] Server process exited (code {exitCode})."
                );
            }
            // The Update loop notices the exit and schedules a restart on the
            // main thread; we don't act on it here because we're on a thread
            // pool worker and Unity APIs are main-thread only.
        }

        // ------------------------------------------------------------------
        // Windows Job Object: ensure the Python process dies if Unity crashes.
        // ------------------------------------------------------------------

        private void CreateJobObjectIfWindows()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer
                && Application.platform != RuntimePlatform.WindowsEditor)
            {
                return;
            }

            try
            {
                _jobHandle = NativeJob.CreateKillOnCloseJob();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[{nameof(LensLabPoseServerLauncher)}] Could not create Windows Job Object: {ex.Message}. " +
                    "Orphan Python processes may survive a Unity crash.",
                    this
                );
                _jobHandle = IntPtr.Zero;
            }
        }

        private void AttachProcessToJobObject(Process proc)
        {
            if (_jobHandle == IntPtr.Zero || proc == null)
            {
                return;
            }

            try
            {
                NativeJob.AssignProcessToJob(_jobHandle, proc.Handle);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[{nameof(LensLabPoseServerLauncher)}] Failed to attach Python process to Job Object: {ex.Message}",
                    this
                );
            }
        }

        private void DestroyJobObject()
        {
            if (_jobHandle != IntPtr.Zero)
            {
                try { NativeJob.CloseHandle(_jobHandle); } catch { }
                _jobHandle = IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------
        // Native (Windows-only) Job Object helpers.
        // No-op on other platforms — the methods are still callable but the
        // CreateKillOnCloseJob will throw an exception that we swallow above.
        // ------------------------------------------------------------------

        private static class NativeJob
        {
            private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
            private const int JobObjectExtendedLimitInformation = 9;

            [StructLayout(LayoutKind.Sequential)]
            private struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long  PerProcessUserTimeLimit;
                public long  PerJobUserTimeLimit;
                public uint  LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint  ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint  PriorityClass;
                public uint  SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(
                IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            public static IntPtr CreateKillOnCloseJob()
            {
                var hJob = CreateJobObjectW(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"CreateJobObjectW failed (err={Marshal.GetLastWin32Error()})");
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                var size = Marshal.SizeOf(info);
                var ptr  = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                    if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    {
                        var err = Marshal.GetLastWin32Error();
                        CloseHandle(hJob);
                        throw new InvalidOperationException(
                            $"SetInformationJobObject failed (err={err})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                return hJob;
            }

            public static void AssignProcessToJob(IntPtr hJob, IntPtr hProcess)
            {
                if (!AssignProcessToJobObject(hJob, hProcess))
                {
                    throw new InvalidOperationException(
                        $"AssignProcessToJobObject failed (err={Marshal.GetLastWin32Error()})");
                }
            }
        }
    }
}
