using UnityEngine;

namespace LensLab.Runtime
{
    public class LensLabPoseLoader : MonoBehaviour
    {
        [Header("Pose Source")]
        [SerializeField] private TextAsset poseJson;
        [SerializeField] private string resourcesPath = "LensLab/pose_003";
        [SerializeField] private bool loadFromResourcesIfMissing = true;

        [Header("Behavior")]
        [SerializeField] private bool loadOnAwake = true;
        [SerializeField] private bool verboseLogging = true;

        public LensLabPoseData LoadedPose { get; private set; }

        public bool HasValidPose => LoadedPose != null && LoadedPose.IsValid();

        private void Awake()
        {
            if (loadOnAwake)
            {
                LoadPose();
            }
        }

        [ContextMenu("Load Pose")]
        public void LoadPose()
        {
            TextAsset source = poseJson;
            if (source == null && loadFromResourcesIfMissing)
            {
                source = Resources.Load<TextAsset>(resourcesPath);
            }

            if (source == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabPoseLoader)}] No pose JSON found. " +
                    "Assign a TextAsset or provide a valid Resources path.",
                    this
                );
                LoadedPose = null;
                return;
            }

            LoadedPose = LoadFromJson(source.text);
            if (LoadedPose == null)
            {
                Debug.LogError($"[{nameof(LensLabPoseLoader)}] Failed to parse pose JSON.", this);
                return;
            }

            if (verboseLogging)
            {
                Debug.Log(BuildDebugSummary(), this);
            }
        }

        public LensLabPoseData LoadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<LensLabPoseData>(json);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
                return null;
            }
        }

        public string BuildDebugSummary()
        {
            if (LoadedPose == null)
            {
                return $"[{nameof(LensLabPoseLoader)}] Pose not loaded.";
            }

            var pose = LoadedPose.pose_estimation;
            return
                $"[{nameof(LensLabPoseLoader)}] Loaded pose for '{LoadedPose.camera_name}'.\n" +
                $"Source Image: {LoadedPose.source_image}\n" +
                $"Translation: tx={pose.tvec[0]:F4}, ty={pose.tvec[1]:F4}, tz={pose.tvec[2]:F4}\n" +
                $"Reprojection: mean={pose.reprojection.mean_error_px:F4}px, max={pose.reprojection.max_error_px:F4}px";
        }
    }
}
