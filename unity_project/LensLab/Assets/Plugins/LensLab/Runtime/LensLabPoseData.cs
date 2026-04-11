using System;
using UnityEngine;

namespace LensLab.Runtime
{
    [Serializable]
    public class LensLabPoseData
    {
        public string schema_version;
        public string camera_name;
        public string source_image;
        public int image_width;
        public int image_height;
        public LensLabPoseEstimation pose_estimation;
        public LensLabPoseBoardModel board_model;
        public LensLabPoseCalibrationTarget calibration_target;
        public LensLabPoseNotes notes;

        public bool IsValid()
        {
            return pose_estimation != null
                && pose_estimation.success
                && pose_estimation.rvec != null
                && pose_estimation.rvec.Length == 3
                && pose_estimation.tvec != null
                && pose_estimation.tvec.Length == 3;
        }

        public Vector3 GetOpenCvTranslation()
        {
            if (pose_estimation?.tvec == null || pose_estimation.tvec.Length < 3)
            {
                return Vector3.zero;
            }

            return new Vector3((float)pose_estimation.tvec[0], (float)pose_estimation.tvec[1], (float)pose_estimation.tvec[2]);
        }

        public Vector3 GetOpenCvRotationVector()
        {
            if (pose_estimation?.rvec == null || pose_estimation.rvec.Length < 3)
            {
                return Vector3.zero;
            }

            return new Vector3((float)pose_estimation.rvec[0], (float)pose_estimation.rvec[1], (float)pose_estimation.rvec[2]);
        }

        public Matrix4x4 GetOpenCvRotationMatrix4x4()
        {
            if (HasParsedRotationMatrix())
            {
                return BuildParsedRotationMatrix();
            }

            return RodriguesToMatrix(GetOpenCvRotationVector());
        }

        public Vector3[] GetPreferredBoardCorners3D()
        {
            if (board_model == null)
            {
                return null;
            }

            if (string.Equals(board_model.preferred_surface, "outer_board", StringComparison.OrdinalIgnoreCase)
                && board_model.outer_board_corners_3d != null
                && board_model.outer_board_corners_3d.Length == 4)
            {
                return ConvertPoints(board_model.outer_board_corners_3d);
            }

            if (board_model.charuco_inner_corners_3d != null && board_model.charuco_inner_corners_3d.Length == 4)
            {
                return ConvertPoints(board_model.charuco_inner_corners_3d);
            }

            if (board_model.outer_board_corners_3d != null && board_model.outer_board_corners_3d.Length == 4)
            {
                return ConvertPoints(board_model.outer_board_corners_3d);
            }

            return null;
        }

        public Vector2[] GetPreferredBoardCornersImage()
        {
            if (board_model == null)
            {
                return null;
            }

            if (string.Equals(board_model.preferred_surface, "outer_board", StringComparison.OrdinalIgnoreCase)
                && board_model.outer_board_corners_image != null
                && board_model.outer_board_corners_image.Length == 4)
            {
                return ConvertPoints(board_model.outer_board_corners_image);
            }

            if (board_model.charuco_inner_corners_image != null && board_model.charuco_inner_corners_image.Length == 4)
            {
                return ConvertPoints(board_model.charuco_inner_corners_image);
            }

            if (board_model.outer_board_corners_image != null && board_model.outer_board_corners_image.Length == 4)
            {
                return ConvertPoints(board_model.outer_board_corners_image);
            }

            return null;
        }

        private static Vector3[] ConvertPoints(LensLabPosePoint3[] points)
        {
            var converted = new Vector3[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                converted[i] = new Vector3(points[i].x, points[i].y, points[i].z);
            }
            return converted;
        }

        private static Vector2[] ConvertPoints(LensLabPosePoint2[] points)
        {
            var converted = new Vector2[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                converted[i] = new Vector2(points[i].x, points[i].y);
            }
            return converted;
        }

        private bool HasParsedRotationMatrix()
        {
            return pose_estimation?.rotation_matrix != null && pose_estimation.rotation_matrix.Length >= 3;
        }

        private Matrix4x4 BuildParsedRotationMatrix()
        {
            var matrix = Matrix4x4.identity;
            for (var row = 0; row < 3; row++)
            {
                var rowValues = pose_estimation.rotation_matrix[row];
                if (rowValues == null || rowValues.Length < 3)
                {
                    continue;
                }

                for (var column = 0; column < 3; column++)
                {
                    matrix[row, column] = (float)rowValues[column];
                }
            }

            return matrix;
        }

        private static Matrix4x4 RodriguesToMatrix(Vector3 rvec)
        {
            var theta = rvec.magnitude;
            if (theta < 1e-8f)
            {
                return Matrix4x4.identity;
            }

            var axis = rvec / theta;
            var x = axis.x;
            var y = axis.y;
            var z = axis.z;
            var cosTheta = Mathf.Cos(theta);
            var sinTheta = Mathf.Sin(theta);
            var oneMinusCos = 1f - cosTheta;

            var matrix = Matrix4x4.identity;
            matrix[0, 0] = cosTheta + x * x * oneMinusCos;
            matrix[0, 1] = x * y * oneMinusCos - z * sinTheta;
            matrix[0, 2] = x * z * oneMinusCos + y * sinTheta;
            matrix[1, 0] = y * x * oneMinusCos + z * sinTheta;
            matrix[1, 1] = cosTheta + y * y * oneMinusCos;
            matrix[1, 2] = y * z * oneMinusCos - x * sinTheta;
            matrix[2, 0] = z * x * oneMinusCos - y * sinTheta;
            matrix[2, 1] = z * y * oneMinusCos + x * sinTheta;
            matrix[2, 2] = cosTheta + z * z * oneMinusCos;
            return matrix;
        }
    }

    [Serializable]
    public class LensLabPoseEstimation
    {
        public bool success;
        public string method;
        public int marker_count;
        public int charuco_corner_count;
        public double[] rvec;
        public double[] tvec;
        public double[][] rotation_matrix;
        public double[] camera_position_in_board_space;
        public LensLabPoseReprojection reprojection;
    }

    [Serializable]
    public class LensLabPoseBoardModel
    {
        public string preferred_surface;
        public LensLabPosePoint3[] outer_board_corners_3d;
        public LensLabPosePoint2[] outer_board_corners_image;
        public LensLabPosePoint3[] charuco_inner_corners_3d;
        public LensLabPosePoint2[] charuco_inner_corners_image;
    }

    [Serializable]
    public class LensLabPosePoint3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class LensLabPosePoint2
    {
        public float x;
        public float y;
    }

    [Serializable]
    public class LensLabPoseReprojection
    {
        public int point_count;
        public double mean_error_px;
        public double max_error_px;
    }

    [Serializable]
    public class LensLabPoseCalibrationTarget
    {
        public string type;
        public int squares_x;
        public int squares_y;
        public double square_size_m;
        public double marker_size_m;
        public string aruco_dictionary;
    }

    [Serializable]
    public class LensLabPoseNotes
    {
        public string coordinate_convention;
        public string board_space_definition;
        public string generated_by;
    }
}
