using UnityEngine;

namespace LensLab.Runtime
{
    public class LensLabPoseVisualizer : MonoBehaviour
    {
        private static readonly Matrix4x4 OpenCvToUnityBasis = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));

        [Header("Dependencies")]
        [SerializeField] private LensLabPoseLoader poseLoader;
        [SerializeField] private Camera referenceCamera;
        [SerializeField] private Transform targetTransform;

        [Header("Behavior")]
        [SerializeField] private bool applyOnAwake = true;
        [SerializeField] private bool keepUpdated = true;
        [SerializeField] private bool verboseLogging = true;

        [Header("Debug")]
        [SerializeField] private bool useLocalSpaceIfNoCamera = false;

        private Vector3 lastPosition;
        private Quaternion lastRotation;

        private void Reset()
        {
            TryAutoAssignDependencies();
        }

        private void Awake()
        {
            TryAutoAssignDependencies();
            if (applyOnAwake)
            {
                ApplyPose();
            }
        }

        private void LateUpdate()
        {
            if (!keepUpdated)
            {
                return;
            }

            ApplyPose();
        }

        [ContextMenu("Apply Pose")]
        public void ApplyPose()
        {
            if (!ValidateSetup(out var poseData))
            {
                return;
            }

            var openCvRotation = poseData.GetOpenCvRotationMatrix4x4();
            var unityRotationMatrix = OpenCvToUnityBasis * openCvRotation * OpenCvToUnityBasis;
            var unityTranslation = OpenCvToUnityBasis.MultiplyPoint3x4(poseData.GetOpenCvTranslation());
            var localRotation = QuaternionFromMatrix(unityRotationMatrix);

            if (referenceCamera != null)
            {
                targetTransform.position = referenceCamera.transform.TransformPoint(unityTranslation);
                targetTransform.rotation = referenceCamera.transform.rotation * localRotation;
            }
            else if (useLocalSpaceIfNoCamera)
            {
                targetTransform.localPosition = unityTranslation;
                targetTransform.localRotation = localRotation;
            }
            else
            {
                targetTransform.position = unityTranslation;
                targetTransform.rotation = localRotation;
            }

            if (verboseLogging && (targetTransform.position != lastPosition || targetTransform.rotation != lastRotation))
            {
                Debug.Log(BuildDebugSummary(unityTranslation, localRotation), this);
            }

            lastPosition = targetTransform.position;
            lastRotation = targetTransform.rotation;
        }

        private void TryAutoAssignDependencies()
        {
            if (poseLoader == null)
            {
                poseLoader = GetComponent<LensLabPoseLoader>();
            }

            if (referenceCamera == null)
            {
                referenceCamera = GetComponent<Camera>();
            }

            if (targetTransform == null)
            {
                targetTransform = transform;
            }
        }

        private bool ValidateSetup(out LensLabPoseData poseData)
        {
            poseData = null;
            TryAutoAssignDependencies();

            if (poseLoader == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabPoseVisualizer)}] Missing pose loader. " +
                    "Assign one explicitly or add LensLabPoseLoader to the same GameObject.",
                    this
                );
                return false;
            }

            if (!poseLoader.HasValidPose)
            {
                poseLoader.LoadPose();
                if (!poseLoader.HasValidPose)
                {
                    Debug.LogError($"[{nameof(LensLabPoseVisualizer)}] Pose data is not available.", this);
                    return false;
                }
            }

            if (targetTransform == null)
            {
                Debug.LogError($"[{nameof(LensLabPoseVisualizer)}] Missing target transform.", this);
                return false;
            }

            poseData = poseLoader.LoadedPose;
            return poseData != null && poseData.IsValid();
        }

        private string BuildDebugSummary(Vector3 unityTranslation, Quaternion localRotation)
        {
            return
                $"[{nameof(LensLabPoseVisualizer)}] Applied OpenCV pose to '{targetTransform.name}'.\n" +
                $"Unity Translation: {unityTranslation}\n" +
                $"Unity Local Rotation: {localRotation.eulerAngles}";
        }

        private static Quaternion QuaternionFromMatrix(Matrix4x4 matrix)
        {
            var forward = matrix.GetColumn(2);
            var upwards = matrix.GetColumn(1);
            if (forward.sqrMagnitude < 1e-8f || upwards.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }

            return Quaternion.LookRotation(forward.normalized, upwards.normalized);
        }
    }
}
