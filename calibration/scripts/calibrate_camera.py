from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

import cv2
import numpy as np
import yaml


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG_PATH = REPO_ROOT / "calibration" / "configs" / "charuco_board.yaml"


@dataclass
class Intrinsics:
    fx: float | None
    fy: float | None
    cx: float | None
    cy: float | None


@dataclass
class ReprojectionError:
    mean: float | None
    rms: float | None


@dataclass
class CalibrationTarget:
    type: str
    squares_x: int
    squares_y: int
    square_size_m: float
    marker_size_m: float | None = None
    aruco_dictionary: str | None = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="LensLab ChArUco calibration pipeline."
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=DEFAULT_CONFIG_PATH,
        help="Path to the YAML calibration config.",
    )
    return parser.parse_args()


def load_yaml(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        data = yaml.safe_load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"Expected a mapping in config file: {path}")
    return data


def resolve_repo_path(path_str: str) -> Path:
    path = Path(path_str)
    if path.is_absolute():
        return path
    return REPO_ROOT / path


def collect_image_paths(config: dict[str, Any]) -> list[Path]:
    input_config = config["input"]
    image_dir = resolve_repo_path(input_config["image_dir"])
    image_glob = input_config["image_glob"]
    return sorted(path for path in image_dir.glob(image_glob) if path.is_file())


def infer_image_size(image_paths: list[Path]) -> tuple[int | None, int | None]:
    if not image_paths:
        return None, None

    image = cv2.imread(str(image_paths[0]), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError(f"Failed to load image: {image_paths[0]}")

    height, width = image.shape[:2]
    return width, height


def get_aruco_dictionary(dictionary_name: str) -> Any:
    if not hasattr(cv2.aruco, dictionary_name):
        raise ValueError(f"Unsupported ArUco dictionary: {dictionary_name}")

    dictionary_id = getattr(cv2.aruco, dictionary_name)
    return cv2.aruco.getPredefinedDictionary(dictionary_id)


def create_charuco_board(config: dict[str, Any]) -> tuple[Any, Any]:
    board_config = config["board"]
    if board_config["type"].lower() != "charuco":
        raise ValueError("Only ChArUco boards are supported in the current script.")

    aruco_dictionary = get_aruco_dictionary(board_config["aruco_dictionary"])
    board = cv2.aruco.CharucoBoard(
        (board_config["squares_x"], board_config["squares_y"]),
        board_config["square_size_m"],
        board_config["marker_size_m"],
        aruco_dictionary,
    )
    return board, aruco_dictionary


def detect_charuco_in_image(
    image_path: Path,
    board: Any,
    aruco_dictionary: Any,
) -> tuple[dict[str, Any], np.ndarray | None, np.ndarray | None]:
    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        return (
            {
                "image_path": str(image_path.relative_to(REPO_ROOT)),
                "status": "load_failed",
                "image_width": None,
                "image_height": None,
                "marker_count": 0,
                "charuco_corner_count": 0,
                "used_for_calibration": False,
            },
            None,
            None,
        )

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    image_height, image_width = gray.shape[:2]

    detector_params = cv2.aruco.DetectorParameters()
    detector = cv2.aruco.ArucoDetector(aruco_dictionary, detector_params)
    marker_corners, marker_ids, _ = detector.detectMarkers(gray)

    marker_count = 0 if marker_ids is None else int(len(marker_ids))
    charuco_corner_count = 0
    status = "markers_not_found"
    used_for_calibration = False
    charuco_corners = None
    charuco_ids = None

    if marker_count > 0:
        _, charuco_corners, charuco_ids = cv2.aruco.interpolateCornersCharuco(
            marker_corners,
            marker_ids,
            gray,
            board,
        )
        if charuco_ids is not None and charuco_corners is not None:
            charuco_corner_count = int(len(charuco_ids))
            if charuco_corner_count > 0:
                status = "charuco_detected"
                used_for_calibration = True
            else:
                status = "markers_only"
        else:
            status = "markers_only"

    detection = {
        "image_path": str(image_path.relative_to(REPO_ROOT)),
        "status": status,
        "image_width": image_width,
        "image_height": image_height,
        "marker_count": marker_count,
        "charuco_corner_count": charuco_corner_count,
        "used_for_calibration": used_for_calibration,
    }
    return detection, charuco_corners, charuco_ids


def detect_charuco_dataset(
    config: dict[str, Any], image_paths: list[Path]
) -> tuple[dict[str, Any], list[np.ndarray], list[np.ndarray], Any]:
    board, aruco_dictionary = create_charuco_board(config)
    detections: list[dict[str, Any]] = []
    all_charuco_corners: list[np.ndarray] = []
    all_charuco_ids: list[np.ndarray] = []

    for image_path in image_paths:
        detection, charuco_corners, charuco_ids = detect_charuco_in_image(
            image_path, board, aruco_dictionary
        )
        detections.append(detection)
        if detection["used_for_calibration"] and charuco_corners is not None and charuco_ids is not None:
            all_charuco_corners.append(charuco_corners)
            all_charuco_ids.append(charuco_ids)

    valid_images = [item for item in detections if item["used_for_calibration"]]
    summary = {
        "board_type": config["board"]["type"],
        "aruco_dictionary": config["board"].get("aruco_dictionary"),
        "detected_image_count": len(valid_images),
        "failed_image_count": len(detections) - len(valid_images),
        "min_required_images": config["calibration"]["min_image_count"],
        "ready_for_calibration": len(valid_images) >= config["calibration"]["min_image_count"],
        "images": detections,
    }
    return summary, all_charuco_corners, all_charuco_ids, board


def build_calibration_flags(config: dict[str, Any]) -> int:
    calibration_config = config["calibration"]
    flags = 0
    if calibration_config.get("fix_principal_point", False):
        flags |= cv2.CALIB_FIX_PRINCIPAL_POINT
    if calibration_config.get("zero_tangent_dist", False):
        flags |= cv2.CALIB_ZERO_TANGENT_DIST
    if calibration_config.get("use_rational_model", False):
        flags |= cv2.CALIB_RATIONAL_MODEL
    return flags


def compute_reprojection_error(
    board: Any,
    all_charuco_corners: list[np.ndarray],
    all_charuco_ids: list[np.ndarray],
    rvecs: list[np.ndarray],
    tvecs: list[np.ndarray],
    camera_matrix: np.ndarray,
    dist_coeffs: np.ndarray,
) -> tuple[float | None, list[dict[str, Any]]]:
    chessboard_corners = board.getChessboardCorners()
    total_error = 0.0
    total_points = 0
    per_view_errors: list[dict[str, Any]] = []

    for index, (charuco_corners, charuco_ids, rvec, tvec) in enumerate(
        zip(all_charuco_corners, all_charuco_ids, rvecs, tvecs)
    ):
        object_points = chessboard_corners[charuco_ids.flatten()]
        projected_points, _ = cv2.projectPoints(
            object_points,
            rvec,
            tvec,
            camera_matrix,
            dist_coeffs,
        )
        projected_points = projected_points.reshape(-1, 2)
        observed_points = charuco_corners.reshape(-1, 2)
        point_errors = np.linalg.norm(observed_points - projected_points, axis=1)
        mean_error = float(point_errors.mean()) if len(point_errors) > 0 else None

        if len(point_errors) > 0:
            total_error += float(point_errors.sum())
            total_points += int(len(point_errors))

        per_view_errors.append(
            {
                "view_index": index,
                "point_count": int(len(point_errors)),
                "mean_error": mean_error,
            }
        )

    mean_reprojection_error = None
    if total_points > 0:
        mean_reprojection_error = total_error / total_points

    return mean_reprojection_error, per_view_errors


def run_charuco_calibration(
    config: dict[str, Any],
    detection_summary: dict[str, Any],
    all_charuco_corners: list[np.ndarray],
    all_charuco_ids: list[np.ndarray],
    board: Any,
    image_size: tuple[int, int] | None,
) -> dict[str, Any]:
    if image_size is None:
        return {
            "success": False,
            "reason": "image_size_unavailable",
            "used_image_count": 0,
            "per_view_errors": [],
        }

    if not detection_summary["ready_for_calibration"]:
        return {
            "success": False,
            "reason": "not_enough_valid_images",
            "used_image_count": len(all_charuco_corners),
            "per_view_errors": [],
        }

    flags = build_calibration_flags(config)
    rms, camera_matrix, dist_coeffs, rvecs, tvecs = cv2.aruco.calibrateCameraCharuco(
        charucoCorners=all_charuco_corners,
        charucoIds=all_charuco_ids,
        board=board,
        imageSize=image_size,
        cameraMatrix=None,
        distCoeffs=None,
        flags=flags,
    )

    mean_error, per_view_errors = compute_reprojection_error(
        board,
        all_charuco_corners,
        all_charuco_ids,
        rvecs,
        tvecs,
        camera_matrix,
        dist_coeffs,
    )

    distortion_values = dist_coeffs.flatten().tolist()
    distortion_map = {
        "k1": None,
        "k2": None,
        "p1": None,
        "p2": None,
        "k3": None,
        "k4": None,
        "k5": None,
        "k6": None,
    }
    distortion_keys = list(distortion_map.keys())
    for index, key in enumerate(distortion_keys):
        if index < len(distortion_values):
            distortion_map[key] = float(distortion_values[index])

    return {
        "success": True,
        "reason": None,
        "used_image_count": len(all_charuco_corners),
        "camera_matrix": camera_matrix,
        "distortion_coeffs": distortion_map,
        "intrinsics": Intrinsics(
            fx=float(camera_matrix[0, 0]),
            fy=float(camera_matrix[1, 1]),
            cx=float(camera_matrix[0, 2]),
            cy=float(camera_matrix[1, 2]),
        ),
        "reprojection_error": ReprojectionError(
            mean=mean_error,
            rms=float(rms),
        ),
        "per_view_errors": per_view_errors,
    }


def build_output_payload(
    config: dict[str, Any],
    image_width: int | None,
    image_height: int | None,
    image_count: int,
    detection_summary: dict[str, Any],
    calibration_result: dict[str, Any],
) -> dict[str, Any]:
    board_config = config["board"]
    target = CalibrationTarget(
        type=board_config["type"],
        squares_x=board_config["squares_x"],
        squares_y=board_config["squares_y"],
        square_size_m=board_config["square_size_m"],
        marker_size_m=board_config.get("marker_size_m"),
        aruco_dictionary=board_config.get("aruco_dictionary"),
    )

    intrinsics = calibration_result.get("intrinsics")
    reprojection_error = calibration_result.get("reprojection_error")
    distortion_coeffs = calibration_result.get(
        "distortion_coeffs",
        {
            "k1": None,
            "k2": None,
            "p1": None,
            "p2": None,
            "k3": None,
            "k4": None,
            "k5": None,
            "k6": None,
        },
    )

    payload = {
        "schema_version": "1.0",
        "camera_name": config["camera_name"],
        "image_width": image_width,
        "image_height": image_height,
        "intrinsics": asdict(intrinsics) if intrinsics else asdict(Intrinsics(None, None, None, None)),
        "distortion_model": config["distortion_model"],
        "distortion_coeffs": distortion_coeffs,
        "calibration_target": asdict(target),
        "reprojection_error": asdict(reprojection_error) if reprojection_error else asdict(ReprojectionError(None, None)),
        "detection_summary": detection_summary,
        "calibration_summary": {
            "success": calibration_result["success"],
            "reason": calibration_result["reason"],
            "used_image_count": calibration_result["used_image_count"],
            "per_view_errors": calibration_result["per_view_errors"],
        },
        "notes": {
            "coordinate_convention": "opencv_camera",
            "generated_by": "LensLab calibration pipeline",
            "status": "calibration_complete" if calibration_result["success"] else "detection_ready",
            "image_count": image_count,
            "todo": "Add undistortion preview export and optional debug overlays.",
        },
    }
    return payload


def write_output_json(config: dict[str, Any], payload: dict[str, Any]) -> Path:
    output_config = config["output"]
    output_dir = resolve_repo_path(output_config["output_dir"])
    output_dir.mkdir(parents=True, exist_ok=True)

    output_path = output_dir / output_config["calibration_json"]
    with output_path.open("w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)
        handle.write("\n")
    return output_path


def main() -> None:
    args = parse_args()
    config = load_yaml(args.config)
    image_paths = collect_image_paths(config)
    image_width, image_height = infer_image_size(image_paths)
    detection_summary, all_charuco_corners, all_charuco_ids, board = detect_charuco_dataset(
        config, image_paths
    )

    image_size = None
    if image_width is not None and image_height is not None:
        image_size = (image_width, image_height)

    calibration_result = run_charuco_calibration(
        config=config,
        detection_summary=detection_summary,
        all_charuco_corners=all_charuco_corners,
        all_charuco_ids=all_charuco_ids,
        board=board,
        image_size=image_size,
    )

    payload = build_output_payload(
        config=config,
        image_width=image_width,
        image_height=image_height,
        image_count=len(image_paths),
        detection_summary=detection_summary,
        calibration_result=calibration_result,
    )
    output_path = write_output_json(config, payload)

    print(f"Loaded config: {args.config}")
    print(f"Discovered images: {len(image_paths)}")
    print(f"Inferred image size: {image_width}x{image_height}")
    print(
        "Detected usable ChArUco frames: "
        f"{detection_summary['detected_image_count']}/{len(image_paths)}"
    )
    print(f"Ready for calibration: {detection_summary['ready_for_calibration']}")
    print(f"Calibration success: {calibration_result['success']}")
    if calibration_result["success"]:
        intrinsics = calibration_result["intrinsics"]
        reprojection_error = calibration_result["reprojection_error"]
        print(
            "Estimated intrinsics: "
            f"fx={intrinsics.fx:.3f}, fy={intrinsics.fy:.3f}, "
            f"cx={intrinsics.cx:.3f}, cy={intrinsics.cy:.3f}"
        )
        print(
            "Reprojection error: "
            f"mean={reprojection_error.mean:.6f}, rms={reprojection_error.rms:.6f}"
        )
    else:
        print(f"Calibration skipped: {calibration_result['reason']}")
    print(f"Wrote calibration JSON: {output_path}")


if __name__ == "__main__":
    main()
