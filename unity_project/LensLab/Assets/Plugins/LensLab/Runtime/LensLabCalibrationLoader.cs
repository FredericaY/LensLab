using UnityEngine;

namespace LensLab.Runtime
{
    public class LensLabCalibrationLoader : MonoBehaviour
    {
        [Header("Calibration Source")]
        [SerializeField] private TextAsset calibrationJson;
        [SerializeField] private string resourcesPath = "LensLab/lenslab_calibration";
        [SerializeField] private bool loadFromResourcesIfMissing = true;

        [Header("Behavior")]
        [SerializeField] private bool loadOnAwake = true;
        [SerializeField] private bool verboseLogging = true;

        public LensLabCalibrationData LoadedCalibration { get; private set; }

        public bool HasValidCalibration => LoadedCalibration != null && LoadedCalibration.IsValid();

        private void Awake()
        {
            if (loadOnAwake)
            {
                LoadCalibration();
            }
        }

        [ContextMenu("Load Calibration")]
        public void LoadCalibration()
        {
            TextAsset source = calibrationJson;
            if (source == null && loadFromResourcesIfMissing)
            {
                source = Resources.Load<TextAsset>(resourcesPath);
            }

            if (source == null)
            {
                Debug.LogError(
                    $"[{nameof(LensLabCalibrationLoader)}] No calibration JSON found. " +
                    "Assign a TextAsset or provide a valid Resources path.",
                    this
                );
                LoadedCalibration = null;
                return;
            }

            LoadedCalibration = LoadFromJson(source.text);
            if (LoadedCalibration == null)
            {
                Debug.LogError($"[{nameof(LensLabCalibrationLoader)}] Failed to parse calibration JSON.", this);
                return;
            }

            if (verboseLogging)
            {
                Debug.Log(BuildDebugSummary(), this);
            }
        }

        public LensLabCalibrationData LoadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<LensLabCalibrationData>(json);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, this);
                return null;
            }
        }

        public string BuildDebugSummary()
        {
            if (LoadedCalibration == null)
            {
                return $"[{nameof(LensLabCalibrationLoader)}] Calibration not loaded.";
            }

            var intrinsics = LoadedCalibration.intrinsics;
            var error = LoadedCalibration.reprojection_error;
            var summary = LoadedCalibration.calibration_summary;

            return
                $"[{nameof(LensLabCalibrationLoader)}] Loaded calibration for '{LoadedCalibration.camera_name}'.\n" +
                $"Image Size: {LoadedCalibration.image_width}x{LoadedCalibration.image_height}\n" +
                $"Intrinsics: fx={intrinsics.fx:F3}, fy={intrinsics.fy:F3}, cx={intrinsics.cx:F3}, cy={intrinsics.cy:F3}\n" +
                $"Distortion: k1={LoadedCalibration.distortion_coeffs.k1:F6}, k2={LoadedCalibration.distortion_coeffs.k2:F6}, p1={LoadedCalibration.distortion_coeffs.p1:F6}, p2={LoadedCalibration.distortion_coeffs.p2:F6}\n" +
                $"Calibration RMS: {error.rms:F6}, Mean Error: {error.mean:F6}\n" +
                $"Used Images: {(summary != null ? summary.used_image_count : 0)}";
        }
    }
}
