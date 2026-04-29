using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// Reads live pose data from <see cref="LensLabPoseClient"/> and applies it to a
    /// target <see cref="Transform"/> every frame, anchoring virtual content on the
    /// detected ChArUco board.
    ///
    /// The pose is expressed in Unity camera space (relative to the Main Camera).
    /// As long as the Unity camera's transform is identity (the default), camera
    /// space equals world space and no extra parenting is needed.
    ///
    /// A simple first-order low-pass filter is applied to smooth jitter.
    /// Set <see cref="positionSmoothing"/> / <see cref="rotationSmoothing"/> to 1
    /// to disable smoothing (snap every frame).
    /// </summary>
    public class LensLabLivePoseReceiver : MonoBehaviour
    {
        [Header("Dependencies")]
        [Tooltip("Reads poses from this client. Auto-assigned if left empty.")]
        [SerializeField] private LensLabPoseClient poseClient;

        [Tooltip("The Transform to move. Defaults to this GameObject's Transform.")]
        [SerializeField] private Transform poseTarget;

        [Header("Filtering")]
        [Tooltip("0 = fully frozen, 1 = snap to every new pose.")]
        [SerializeField] [Range(0f, 1f)] private float positionSmoothing = 0.3f;

        [Tooltip("0 = fully frozen, 1 = snap to every new pose.")]
        [SerializeField] [Range(0f, 1f)] private float rotationSmoothing = 0.3f;

        [Header("Visibility")]
        [Tooltip("Hide the target GameObject when no board is detected in the current frame.")]
        [SerializeField] private bool hideWhenNotDetected = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation = Quaternion.identity;
        private bool _hasFirstPose;

        // Track the last pose we applied so we can skip identical frames.
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

            // No data yet, or board not visible.
            if (pose == null || !pose.IsValid)
            {
                if (hideWhenNotDetected && poseTarget != null)
                {
                    poseTarget.gameObject.SetActive(false);
                }

                return;
            }

            // Board detected — make sure the target is visible.
            if (poseTarget != null && !poseTarget.gameObject.activeSelf)
            {
                poseTarget.gameObject.SetActive(true);
            }

            // Skip if this is the exact same pose object we already processed.
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
            var targetPosition = pose.UnityPosition;
            var targetRotation = pose.UnityRotation;

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

            if (verboseLogging)
            {
                Debug.Log(
                    $"[{nameof(LensLabLivePoseReceiver)}] Pose applied: " +
                    $"pos={_smoothedPosition:F3}  " +
                    $"err={pose.reprojection_error:F2}px  " +
                    $"corners={pose.charuco_corner_count}",
                    this
                );
            }
        }

        // ------------------------------------------------------------------
        // Auto-wiring
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
        }
    }
}
