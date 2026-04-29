using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Reads live pose data from <see cref="LensLabPoseClient"/> and places a target
    /// <see cref="Transform"/> so that virtual content appears correctly anchored on
    /// the detected ChArUco board every frame.
    ///
    /// Board anchor offset
    /// -------------------
    /// OpenCV's pose origin is the board's (0,0) corner, not its centre. Use
    /// <see cref="boardLocalOffset"/> to shift the anchor to any board-local position.
    /// The default value centres the anchor on a 7 × 5 board at 0.025 m/square
    /// (offset = (0.0875, 0.0625, 0) metres in board space).
    ///
    /// Board scale matching
    /// --------------------
    /// When <see cref="matchBoardScale"/> is enabled the target's local scale is set
    /// to the physical board dimensions so that a unit Quad perfectly covers the board.
    ///
    /// Coordinate space
    /// ----------------
    /// Positions are in Unity camera space. With the default camera transform (at the
    /// world origin, no rotation) camera space equals world space and no extra
    /// parenting is required.
    ///
    /// Smoothing
    /// ---------
    /// A first-order low-pass filter reduces jitter. Set each smoothing value to 1 to
    /// snap immediately to every new pose.
    /// </summary>
    public class LensLabLivePoseReceiver : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // Inspector
        // ------------------------------------------------------------------

        [Header("Dependencies")]
        [Tooltip("Source of live pose data. Auto-assigned from the scene if left empty.")]
        [SerializeField] private LensLabPoseClient poseClient;

        [Tooltip("The Transform to drive. Defaults to this GameObject's own Transform.")]
        [SerializeField] private Transform poseTarget;

        [Tooltip("Camera whose local space receives the OpenCV pose. Defaults to Camera.main.")]
        [SerializeField] private Camera targetCamera;

        [Header("Board Anchor")]
        [Tooltip(
            "Offset from the board's (0,0) corner to the desired anchor point, " +
            "expressed in board local space (OpenCV convention: X right, Y down, Z out of surface), " +
            "in metres. Default centres the anchor on a 7x5 board at 0.025 m/square."
        )]
        [SerializeField] private Vector3 boardLocalOffset = new Vector3(0.0875f, 0.0625f, 0f);

        [Header("Board Scale")]
        [Tooltip(
            "When enabled, the target's local scale is set to the board's physical size so that " +
            "a unit Quad (1x1) exactly covers the board surface."
        )]
        [SerializeField] private bool matchBoardScale = true;

        [Tooltip("Physical board size in metres (width = X, height = Y). " +
                 "Default matches a 7x5 board at 0.025 m/square: 0.175 x 0.125 m.")]
        [SerializeField] private Vector2 boardSizeMeters = new Vector2(0.175f, 0.125f);

        [Tooltip("Scale applied along the local Z axis when Match Board Scale is on. " +
                 "Keep small; only affects 3D objects that have depth.")]
        [SerializeField] private float boardDepthScale = 0.01f;

        [Header("Filtering")]
        [Tooltip("0 = frozen, 1 = snap to every new pose.")]
        [SerializeField] [Range(0f, 1f)] private float positionSmoothing = 0.3f;

        [Tooltip("0 = frozen, 1 = snap to every new pose.")]
        [SerializeField] [Range(0f, 1f)] private float rotationSmoothing = 0.3f;

        [Header("Visibility")]
        [Tooltip("Hide the target when no board is detected.")]
        [SerializeField] private bool hideWhenNotDetected = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation = Quaternion.identity;
        private bool _hasFirstPose;
        private LensLabLivePoseData _lastAppliedPose;

        // ------------------------------------------------------------------
        // Unity lifecycle
        // ------------------------------------------------------------------

        private void Start()
        {
            TryAutoAssignDependencies();

            if (poseTarget == null)
            {
                poseTarget = transform;
            }

            _smoothedPosition = poseTarget.position;
            _smoothedRotation = poseTarget.rotation;
        }

        private void Update()
        {
            TryAutoAssignDependencies();

            if (poseClient == null)
            {
                return;
            }

            var pose = poseClient.LatestPose;

            if (pose == null || !pose.IsValid)
            {
                SetTargetVisible(false);
                return;
            }

            SetTargetVisible(true);

            if (ReferenceEquals(pose, _lastAppliedPose))
            {
                return;
            }

            _lastAppliedPose = pose;
            ApplyPose(pose);
        }

        // ------------------------------------------------------------------
        // Pose application
        // ------------------------------------------------------------------

        private void ApplyPose(LensLabLivePoseData pose)
        {
            // The pose is expressed in Unity camera space, so convert it through
            // the actual scene camera before writing a world-space Transform.
            var cameraSpacePosition = pose.BoardLocalToUnityCamera(boardLocalOffset);
            var cameraSpaceRotation = pose.UnityRotation;
            var targetPosition = targetCamera != null
                ? targetCamera.transform.TransformPoint(cameraSpacePosition)
                : cameraSpacePosition;
            var targetRotation = targetCamera != null
                ? targetCamera.transform.rotation * cameraSpaceRotation
                : cameraSpaceRotation;

            if (!_hasFirstPose)
            {
                _smoothedPosition = targetPosition;
                _smoothedRotation = targetRotation;
                _hasFirstPose = true;
            }
            else
            {
                _smoothedPosition = Vector3.Lerp(_smoothedPosition, targetPosition, positionSmoothing);
                _smoothedRotation = Quaternion.Slerp(_smoothedRotation, targetRotation, rotationSmoothing);
            }

            poseTarget.position = _smoothedPosition;
            poseTarget.rotation = _smoothedRotation;

            if (matchBoardScale && boardSizeMeters.x > 0f && boardSizeMeters.y > 0f)
            {
                poseTarget.localScale = new Vector3(
                    boardSizeMeters.x,
                    boardSizeMeters.y,
                    boardDepthScale
                );
            }

            if (verboseLogging)
            {
                Debug.Log(
                    $"[{nameof(LensLabLivePoseReceiver)}] " +
                    $"pos={_smoothedPosition:F3}  " +
                    $"err={pose.reprojection_error:F2}px  " +
                    $"corners={pose.charuco_corner_count}",
                    this
                );
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void SetTargetVisible(bool visible)
        {
            if (!hideWhenNotDetected || poseTarget == null)
            {
                return;
            }

            if (poseTarget.gameObject.activeSelf != visible)
            {
                poseTarget.gameObject.SetActive(visible);
            }
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

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
        }
    }
}
