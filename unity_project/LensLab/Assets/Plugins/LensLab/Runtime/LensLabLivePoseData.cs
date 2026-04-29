using System;
using UnityEngine;

namespace LensLab.Runtime
{
    /// <summary>
    /// One pose response received from the Python pose server.
    /// Deserialised directly from the server's JSON via JsonUtility.
    /// rotation_matrix_flat is a row-major, 9-element array (3 x 3).
    /// </summary>
    [Serializable]
    public class LensLabLivePoseData
    {
        public bool detected;
        public int marker_count;
        public int charuco_corner_count;

        /// <summary>Rodrigues rotation vector in OpenCV convention (3 elements).</summary>
        public float[] rvec;

        /// <summary>Translation vector in OpenCV camera space, metres (3 elements).</summary>
        public float[] tvec;

        /// <summary>3x3 rotation matrix, row-major (9 elements). R maps board space → OpenCV camera space.</summary>
        public float[] rotation_matrix_flat;

        /// <summary>Mean reprojection error in pixels for this frame.</summary>
        public float reprojection_error;

        /// <summary>Non-empty only when the server encountered a processing error.</summary>
        public string error;

        // ------------------------------------------------------------------
        // Convenience accessors
        // ------------------------------------------------------------------

        public bool IsValid =>
            detected
            && tvec != null && tvec.Length == 3
            && rotation_matrix_flat != null && rotation_matrix_flat.Length == 9;

        /// <summary>
        /// Board origin in OpenCV camera space (metres).
        /// tvec directly gives the board's (0,0,0) corner in camera coordinates.
        /// </summary>
        public Vector3 OpenCvTranslation =>
            tvec != null && tvec.Length >= 3
                ? new Vector3(tvec[0], tvec[1], tvec[2])
                : Vector3.zero;

        /// <summary>
        /// Rotation matrix element at (row, col), 0-indexed.
        /// </summary>
        public float RotationElement(int row, int col) =>
            rotation_matrix_flat != null && rotation_matrix_flat.Length == 9
                ? rotation_matrix_flat[row * 3 + col]
                : (row == col ? 1f : 0f);

        // ------------------------------------------------------------------
        // OpenCV → Unity coordinate conversion
        // ------------------------------------------------------------------
        //
        // OpenCV camera frame:  X right, Y down,  Z forward (right-handed)
        // Unity  camera frame:  X right, Y up,    Z forward (left-handed)
        //
        // Coordinate mapping: flip Y.
        //   position_unity = (tx,  -ty,  tz)
        //
        // For rotation, apply the same Y-flip to both source (board) and
        // target (camera) spaces:
        //   R_unity = M * R_opencv * M   where M = diag(1, -1, 1)
        // This negates every matrix element that touches the Y row or Y column
        // exactly once (i.e. elements [0,1] [1,0] [1,2] [2,1] are negated;
        // element [1,1] is double-negated and stays positive).
        //
        // ------------------------------------------------------------------

        /// <summary>
        /// Board origin position expressed in Unity camera space (metres).
        /// Place a virtual object at this position (relative to the Unity camera)
        /// to anchor it at the board's (0,0,0) corner.
        /// </summary>
        public Vector3 UnityPosition =>
            tvec != null && tvec.Length >= 3
                ? new Vector3(tvec[0], -tvec[1], tvec[2])
                : Vector3.zero;

        /// <summary>
        /// Board orientation expressed as a Unity Quaternion in camera space.
        /// Combined with UnityPosition this fully locates the board.
        /// </summary>
        public Quaternion UnityRotation
        {
            get
            {
                if (rotation_matrix_flat == null || rotation_matrix_flat.Length != 9)
                {
                    return Quaternion.identity;
                }

                // R_unity = M * R_opencv * M  (M = diag(1,-1,1))
                // Negated elements: [0,1], [1,0], [1,2], [2,1]
                var m = rotation_matrix_flat;
                var mat = new Matrix4x4();
                mat[0, 0] =  m[0]; mat[0, 1] = -m[1]; mat[0, 2] =  m[2]; mat[0, 3] = 0f;
                mat[1, 0] = -m[3]; mat[1, 1] =  m[4]; mat[1, 2] = -m[5]; mat[1, 3] = 0f;
                mat[2, 0] =  m[6]; mat[2, 1] = -m[7]; mat[2, 2] =  m[8]; mat[2, 3] = 0f;
                mat[3, 0] =  0f;   mat[3, 1] =  0f;   mat[3, 2] =  0f;   mat[3, 3] = 1f;
                return mat.rotation;
            }
        }
    }
}
