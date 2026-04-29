using UnityEngine;
using UnityEngine.UI;

namespace LensLab.Runtime
{
    /// <summary>
    /// Displays live LensLab status information in a screen-space HUD panel.
    ///
    /// Reads <see cref="LensLabPoseClient.IsConnected"/> and
    /// <see cref="LensLabPoseClient.LatestPose"/> every frame and updates the
    /// three UI Text fields created by the Editor setup wizard.
    ///
    /// Typical layout (created automatically by LensLab → Setup → Create Live AR Scene):
    /// <code>
    ///   [ LensLab ]
    ///   Server  ● Connected
    ///   Board   ● Detected  (18 corners)
    ///   Error   0.82 px
    /// </code>
    /// </summary>
    public class LensLabStatusHUD : MonoBehaviour
    {
        [Header("UI Text Fields")]
        [SerializeField] private Text serverStatusText;
        [SerializeField] private Text boardStatusText;
        [SerializeField] private Text metricsText;

        [Header("Dependencies")]
        [SerializeField] private LensLabPoseClient poseClient;

        // Cached colours expressed as rich-text strings for readability.
        private const string Green  = "#44EE66";
        private const string Red    = "#FF4433";
        private const string Yellow = "#FFCC22";
        private const string Grey   = "#AAAAAA";

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            TryAutoAssignDependencies();
        }

        private void Update()
        {
            TryAutoAssignDependencies();
            RefreshUI();
        }

        // ------------------------------------------------------------------
        // UI refresh
        // ------------------------------------------------------------------

        private void RefreshUI()
        {
            if (poseClient == null)
            {
                SetLine(serverStatusText, "Server", "No client", Grey);
                SetLine(boardStatusText,  "Board",  "—",         Grey);
                ClearLine(metricsText);
                return;
            }

            // --- Server line ---
            if (poseClient.IsConnected)
            {
                SetLine(serverStatusText, "Server", "Connected", Green);
            }
            else
            {
                SetLine(serverStatusText, "Server", "Disconnected", Red);
            }

            // --- Board + metrics lines ---
            var pose = poseClient.LatestPose;

            if (pose == null)
            {
                SetLine(boardStatusText, "Board", "Waiting…", Yellow);
                ClearLine(metricsText);
                return;
            }

            if (pose.detected)
            {
                SetLine(
                    boardStatusText,
                    "Board",
                    $"Detected  <color={Grey}>({pose.charuco_corner_count} corners)</color>",
                    Green
                );
                SetPlain(
                    metricsText,
                    $"<color={Grey}>Error   </color>{pose.reprojection_error:F2} px"
                );
            }
            else
            {
                SetLine(boardStatusText, "Board", "Not detected", Red);
                ClearLine(metricsText);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Writes "Label  ● Value" with a coloured bullet.</summary>
        private static void SetLine(Text field, string label, string value, string hexColour)
        {
            if (field == null) return;
            field.text = $"<color={Grey}>{label,-8}</color><color={hexColour}>● </color>{value}";
        }

        /// <summary>Writes a plain rich-text string (no bullet).</summary>
        private static void SetPlain(Text field, string content)
        {
            if (field == null) return;
            field.text = content;
        }

        private static void ClearLine(Text field)
        {
            if (field == null) return;
            field.text = string.Empty;
        }

        private void TryAutoAssignDependencies()
        {
            if (poseClient == null)
            {
                poseClient = FindObjectOfType<LensLabPoseClient>(true);
            }
        }
    }
}
