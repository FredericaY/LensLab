using System;
using UnityEngine;

namespace LensLab.Runtime
{
    [Serializable]
    public class LensLabCalibrationData
    {
        public string schema_version;
        public string camera_name;
        public int image_width;
        public int image_height;
        public LensLabIntrinsics intrinsics;
        public string distortion_model;
        public LensLabDistortionCoefficients distortion_coeffs;
        public LensLabCalibrationTarget calibration_target;
        public LensLabReprojectionError reprojection_error;
        public LensLabDetectionSummary detection_summary;
        public LensLabCalibrationSummary calibration_summary;
        public LensLabNotes notes;

        public bool IsValid()
        {
            return intrinsics != null
                && distortion_coeffs != null
                && image_width > 0
                && image_height > 0;
        }

        public Matrix4x4 CreateCameraMatrix4x4()
        {
            var matrix = Matrix4x4.identity;
            matrix[0, 0] = intrinsics != null ? (float)intrinsics.fx : 0f;
            matrix[1, 1] = intrinsics != null ? (float)intrinsics.fy : 0f;
            matrix[0, 2] = intrinsics != null ? (float)intrinsics.cx : 0f;
            matrix[1, 2] = intrinsics != null ? (float)intrinsics.cy : 0f;
            return matrix;
        }

        public Vector4 GetPrimaryDistortionVector()
        {
            if (distortion_coeffs == null)
            {
                return Vector4.zero;
            }

            return new Vector4(
                (float)distortion_coeffs.k1,
                (float)distortion_coeffs.k2,
                (float)distortion_coeffs.p1,
                (float)distortion_coeffs.p2
            );
        }

        public override string ToString()
        {
            var intrinsicsText = intrinsics == null
                ? "intrinsics=missing"
                : $"fx={intrinsics.fx:F3}, fy={intrinsics.fy:F3}, cx={intrinsics.cx:F3}, cy={intrinsics.cy:F3}";

            var errorText = reprojection_error == null
                ? "error=missing"
                : $"mean={reprojection_error.mean:F4}, rms={reprojection_error.rms:F4}";

            return $"LensLabCalibrationData(camera={camera_name}, size={image_width}x{image_height}, {intrinsicsText}, {errorText})";
        }
    }

    [Serializable]
    public class LensLabIntrinsics
    {
        public double fx;
        public double fy;
        public double cx;
        public double cy;
    }

    [Serializable]
    public class LensLabDistortionCoefficients
    {
        public double k1;
        public double k2;
        public double p1;
        public double p2;
        public double k3;
        public double k4;
        public double k5;
        public double k6;
    }

    [Serializable]
    public class LensLabCalibrationTarget
    {
        public string type;
        public int squares_x;
        public int squares_y;
        public double square_size_m;
        public double marker_size_m;
        public string aruco_dictionary;
    }

    [Serializable]
    public class LensLabReprojectionError
    {
        public double mean;
        public double rms;
    }

    [Serializable]
    public class LensLabDetectionSummary
    {
        public string board_type;
        public string aruco_dictionary;
        public int detected_image_count;
        public int failed_image_count;
        public int min_required_images;
        public bool ready_for_calibration;
        public LensLabDetectionImage[] images;
    }

    [Serializable]
    public class LensLabDetectionImage
    {
        public string image_path;
        public string status;
        public int image_width;
        public int image_height;
        public int marker_count;
        public int charuco_corner_count;
        public bool used_for_calibration;
    }

    [Serializable]
    public class LensLabCalibrationSummary
    {
        public bool success;
        public string reason;
        public int used_image_count;
        public LensLabPerViewError[] per_view_errors;
    }

    [Serializable]
    public class LensLabPerViewError
    {
        public int view_index;
        public int point_count;
        public double mean_error;
    }

    [Serializable]
    public class LensLabNotes
    {
        public string coordinate_convention;
        public string generated_by;
        public string status;
        public int image_count;
        public string todo;
    }
}
