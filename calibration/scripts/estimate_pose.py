from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import cv2
import numpy as np
import yaml


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG_PATH = REPO_ROOT / "calibration" / "configs" / "charuco_board.yaml"
DEFAULT_CALIBRATION_PATH = REPO_ROOT / "calibration" / "output" / "camera_calibration.json"
DEFAULT_OUTPUT_DIR = REPO_ROOT / "calibration" / "output" / "pose_estimation"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Estimate ChArUco board pose from a calibrated image."
    )
    parser.add_argument("--image", type=Path, required=True, help="Path to the input image used for pose estimation.")
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG_PATH, help="Path to the YAML ChArUco board config.")
    parser.add_argument("--calibration", type=Path, default=DEFAULT_CALIBRATION_PATH, help="Path to the camera calibration JSON.")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR, help="Directory for pose JSON and debug image outputs.")
    parser.add_argument("--debug-image", action="store_true", help="Also write a debug image with detected corners and pose axes.")
    parser.add_argument("--axis-length-m", type=float, default=0.08, help="Axis length in meters for debug rendering.")
    return parser.parse_args()


def resolve_repo_path(path: Path) -> Path:
    return path if path.is_absolute() else REPO_ROOT / path


def load_yaml(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        data = yaml.safe_load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"Expected a mapping in config file: {path}")
    return data


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"Expected a mapping in JSON file: {path}")
    return data


def get_aruco_dictionary(dictionary_name: str) -> Any:
    if not hasattr(cv2.aruco, dictionary_name):
        raise ValueError(f"Unsupported ArUco dictionary: {dictionary_name}")
    return cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, dictionary_name))


def create_charuco_board(config: dict[str, Any]) -> tuple[Any, Any]:
    board_config = config["board"]
    if board_config["type"].lower() != "charuco":
        raise ValueError("Only ChArUco boards are supported in the current pose script.")

    aruco_dictionary = get_aruco_dictionary(board_config["aruco_dictionary"])
    board = cv2.aruco.CharucoBoard(
        (board_config["squares_x"], board_config["squares_y"]),
        board_config["square_size_m"],
        board_config["marker_size_m"],
        aruco_dictionary,
    )
    return board, aruco_dictionary


def build_camera_matrix(calibration: dict[str, Any]) -> np.ndarray:
    intrinsics = calibration["intrinsics"]
    return np.array(
        [
            [float(intrinsics["fx"]), 0.0, float(intrinsics["cx"])],
            [0.0, float(intrinsics["fy"]), float(intrinsics["cy"])],
            [0.0, 0.0, 1.0],
        ],
        dtype=np.float64,
    )


def build_distortion_vector(calibration: dict[str, Any]) -> np.ndarray:
    coeffs = calibration["distortion_coeffs"]
    ordered = [
        coeffs.get("k1", 0.0),
        coeffs.get("k2", 0.0),
        coeffs.get("p1", 0.0),
        coeffs.get("p2", 0.0),
        coeffs.get("k3", 0.0),
        coeffs.get("k4", 0.0),
        coeffs.get("k5", 0.0),
        coeffs.get("k6", 0.0),
    ]
    return np.array([0.0 if value is None else float(value) for value in ordered], dtype=np.float64)


def detect_charuco(image: np.ndarray, board: Any, aruco_dictionary: Any) -> tuple[list[np.ndarray], np.ndarray | None, np.ndarray | None]:
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    detector = cv2.aruco.ArucoDetector(aruco_dictionary, cv2.aruco.DetectorParameters())
    marker_corners, marker_ids, _ = detector.detectMarkers(gray)

    if marker_ids is None or len(marker_ids) == 0:
        return marker_corners, None, None

    _, charuco_corners, charuco_ids = cv2.aruco.interpolateCornersCharuco(marker_corners, marker_ids, gray, board)
    return marker_corners, charuco_corners, charuco_ids


def estimate_pose(board: Any, charuco_corners: np.ndarray, charuco_ids: np.ndarray, camera_matrix: np.ndarray, dist_coeffs: np.ndarray) -> tuple[bool, np.ndarray | None, np.ndarray | None]:
    chessboard_corners = board.getChessboardCorners()
    object_points = chessboard_corners[charuco_ids.flatten()].reshape(-1, 1, 3)
    image_points = charuco_corners.reshape(-1, 1, 2)
    success, rvec, tvec = cv2.solvePnP(object_points, image_points, camera_matrix, dist_coeffs, flags=cv2.SOLVEPNP_ITERATIVE)
    return bool(success), rvec, tvec


def compute_pose_reprojection_error(object_points: np.ndarray, image_points: np.ndarray, rvec: np.ndarray, tvec: np.ndarray, camera_matrix: np.ndarray, dist_coeffs: np.ndarray) -> dict[str, float | int]:
    projected_points, _ = cv2.projectPoints(object_points, rvec, tvec, camera_matrix, dist_coeffs)
    projected_points = projected_points.reshape(-1, 2)
    observed_points = image_points.reshape(-1, 2)
    errors = np.linalg.norm(observed_points - projected_points, axis=1)
    return {
        "point_count": int(len(errors)),
        "mean_error_px": float(errors.mean()) if len(errors) > 0 else 0.0,
        "max_error_px": float(errors.max()) if len(errors) > 0 else 0.0,
    }


def points3_to_payload(points: np.ndarray) -> list[dict[str, float]]:
    return [{"x": float(p[0]), "y": float(p[1]), "z": float(p[2])} for p in points]


def points2_to_payload(points: np.ndarray) -> list[dict[str, float]]:
    return [{"x": float(p[0]), "y": float(p[1])} for p in points]


def build_board_model(board: Any, config: dict[str, Any], rvec: np.ndarray, tvec: np.ndarray, camera_matrix: np.ndarray, dist_coeffs: np.ndarray) -> dict[str, Any]:
    board_config = config["board"]
    square_size = float(board_config["square_size_m"])
    chessboard_corners = board.getChessboardCorners().reshape(-1, 3)

    inner_min = chessboard_corners.min(axis=0)
    inner_max = chessboard_corners.max(axis=0)
    inner_corners = np.array(
        [
            [inner_min[0], inner_min[1], 0.0],
            [inner_max[0], inner_min[1], 0.0],
            [inner_max[0], inner_max[1], 0.0],
            [inner_min[0], inner_max[1], 0.0],
        ],
        dtype=np.float32,
    )

    if hasattr(board, "getRightBottomCorner"):
        right_bottom = np.array(board.getRightBottomCorner(), dtype=np.float32).reshape(-1)
        outer_corners = np.array(
            [
                [0.0, 0.0, 0.0],
                [right_bottom[0], 0.0, 0.0],
                [right_bottom[0], right_bottom[1], 0.0],
                [0.0, right_bottom[1], 0.0],
            ],
            dtype=np.float32,
        )
    else:
        outer_corners = np.array(
            [
                [inner_min[0] - square_size * 0.5, inner_min[1] - square_size * 0.5, 0.0],
                [inner_max[0] + square_size * 0.5, inner_min[1] - square_size * 0.5, 0.0],
                [inner_max[0] + square_size * 0.5, inner_max[1] + square_size * 0.5, 0.0],
                [inner_min[0] - square_size * 0.5, inner_max[1] + square_size * 0.5, 0.0],
            ],
            dtype=np.float32,
        )

    outer_image, _ = cv2.projectPoints(outer_corners.reshape(-1, 1, 3), rvec, tvec, camera_matrix, dist_coeffs)
    inner_image, _ = cv2.projectPoints(inner_corners.reshape(-1, 1, 3), rvec, tvec, camera_matrix, dist_coeffs)

    return {
        "preferred_surface": "outer_board",
        "outer_board_corners_3d": points3_to_payload(outer_corners),
        "outer_board_corners_image": points2_to_payload(outer_image.reshape(-1, 2)),
        "charuco_inner_corners_3d": points3_to_payload(inner_corners),
        "charuco_inner_corners_image": points2_to_payload(inner_image.reshape(-1, 2)),
    }


def build_pose_payload(image_path: Path, image_shape: tuple[int, int], calibration: dict[str, Any], config: dict[str, Any], marker_count: int, charuco_corner_count: int, rvec: np.ndarray, tvec: np.ndarray, reprojection_summary: dict[str, float | int], board_model: dict[str, Any]) -> dict[str, Any]:
    rotation_matrix, _ = cv2.Rodrigues(rvec)
    camera_position_board = (-rotation_matrix.T @ tvec).reshape(-1)

    return {
        "schema_version": "1.1",
        "camera_name": calibration.get("camera_name", config.get("camera_name", "default_camera")),
        "source_image": str(image_path.relative_to(REPO_ROOT)),
        "image_width": int(image_shape[1]),
        "image_height": int(image_shape[0]),
        "pose_estimation": {
            "success": True,
            "method": "charuco_solvepnp",
            "marker_count": marker_count,
            "charuco_corner_count": charuco_corner_count,
            "rvec": [float(x) for x in rvec.reshape(-1)],
            "tvec": [float(x) for x in tvec.reshape(-1)],
            "rotation_matrix": [[float(v) for v in row] for row in rotation_matrix.tolist()],
            "camera_position_in_board_space": [float(x) for x in camera_position_board.tolist()],
            "reprojection": reprojection_summary,
        },
        "board_model": board_model,
        "calibration_target": config["board"],
        "notes": {
            "coordinate_convention": "opencv_camera",
            "board_space_definition": "Board geometry exported directly from OpenCV CharucoBoard definitions.",
            "generated_by": "LensLab pose estimation pipeline",
        },
    }


def draw_pose_debug(image: np.ndarray, marker_corners: list[np.ndarray], charuco_corners: np.ndarray | None, charuco_ids: np.ndarray | None, camera_matrix: np.ndarray, dist_coeffs: np.ndarray, rvec: np.ndarray, tvec: np.ndarray, axis_length_m: float) -> np.ndarray:
    debug_image = image.copy()
    if marker_corners:
        cv2.aruco.drawDetectedMarkers(debug_image, marker_corners)
    if charuco_corners is not None and charuco_ids is not None:
        cv2.aruco.drawDetectedCornersCharuco(debug_image, charuco_corners, charuco_ids)
    cv2.drawFrameAxes(debug_image, camera_matrix, dist_coeffs, rvec, tvec, axis_length_m, 2)
    return debug_image


def write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)
        handle.write("\n")


def main() -> None:
    args = parse_args()
    image_path = resolve_repo_path(args.image)
    config_path = resolve_repo_path(args.config)
    calibration_path = resolve_repo_path(args.calibration)
    output_dir = resolve_repo_path(args.output_dir)

    config = load_yaml(config_path)
    calibration = load_json(calibration_path)
    board, aruco_dictionary = create_charuco_board(config)
    camera_matrix = build_camera_matrix(calibration)
    dist_coeffs = build_distortion_vector(calibration)

    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError(f"Failed to load image: {image_path}")

    marker_corners, charuco_corners, charuco_ids = detect_charuco(image, board, aruco_dictionary)
    marker_count = len(marker_corners)
    charuco_corner_count = 0 if charuco_ids is None else int(len(charuco_ids))

    if charuco_corners is None or charuco_ids is None or len(charuco_ids) < 4:
        raise RuntimeError(
            "Insufficient ChArUco detections for pose estimation. "
            f"markers={marker_count}, charuco_corners={charuco_corner_count}"
        )

    success, rvec, tvec = estimate_pose(board, charuco_corners, charuco_ids, camera_matrix, dist_coeffs)
    if not success or rvec is None or tvec is None:
        raise RuntimeError("cv2.solvePnP failed to estimate a valid pose.")

    object_points = board.getChessboardCorners()[charuco_ids.flatten()].reshape(-1, 1, 3)
    reprojection_summary = compute_pose_reprojection_error(object_points, charuco_corners, rvec, tvec, camera_matrix, dist_coeffs)
    board_model = build_board_model(board, config, rvec, tvec, camera_matrix, dist_coeffs)

    payload = build_pose_payload(
        image_path=image_path,
        image_shape=image.shape[:2],
        calibration=calibration,
        config=config,
        marker_count=marker_count,
        charuco_corner_count=charuco_corner_count,
        rvec=rvec,
        tvec=tvec,
        reprojection_summary=reprojection_summary,
        board_model=board_model,
    )

    output_dir.mkdir(parents=True, exist_ok=True)
    stem = image_path.stem
    pose_json_path = output_dir / f"{stem}_pose.json"
    write_json(pose_json_path, payload)

    debug_path = None
    if args.debug_image:
        debug_image = draw_pose_debug(image, marker_corners, charuco_corners, charuco_ids, camera_matrix, dist_coeffs, rvec, tvec, args.axis_length_m)
        debug_path = output_dir / f"{stem}_pose_debug.png"
        cv2.imwrite(str(debug_path), debug_image)

    print(f"Image: {image_path}")
    print(f"Calibration: {calibration_path}")
    print(f"Markers detected: {marker_count}")
    print(f"ChArUco corners detected: {charuco_corner_count}")
    tvec_values = tvec.reshape(-1)
    rvec_values = rvec.reshape(-1)
    print(f"Pose translation (board -> camera): tx={float(tvec_values[0]):.6f}, ty={float(tvec_values[1]):.6f}, tz={float(tvec_values[2]):.6f}")
    print(f"Pose rotation vector: rx={float(rvec_values[0]):.6f}, ry={float(rvec_values[1]):.6f}, rz={float(rvec_values[2]):.6f}")
    print(f"Reprojection: mean={reprojection_summary['mean_error_px']:.6f}px, max={reprojection_summary['max_error_px']:.6f}px")
    print(f"Wrote pose JSON: {pose_json_path}")
    if debug_path is not None:
        print(f"Wrote debug image: {debug_path}")


if __name__ == "__main__":
    main()
