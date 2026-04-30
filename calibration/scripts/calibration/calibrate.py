from __future__ import annotations

import argparse
import json
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Sequence


REPO_ROOT = Path(__file__).resolve().parents[3]
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


@dataclass
class CharucoObservation:
    corners: np.ndarray
    ids: np.ndarray
    metadata: dict[str, Any]


def load_yaml(path: Path) -> dict[str, Any]:
    import yaml

    with path.open("r", encoding="utf-8") as handle:
        data = yaml.safe_load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"Expected a mapping in config file: {path}")
    return data


def resolve_repo_path(path: str | Path) -> Path:
    resolved = Path(path)
    if resolved.is_absolute():
        return resolved
    return REPO_ROOT / resolved


def repo_display_path(path: Path) -> str:
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def get_aruco_dictionary(dictionary_name: str) -> Any:
    import cv2

    if not hasattr(cv2.aruco, dictionary_name):
        raise ValueError(f"Unsupported ArUco dictionary: {dictionary_name}")

    dictionary_id = getattr(cv2.aruco, dictionary_name)
    return cv2.aruco.getPredefinedDictionary(dictionary_id)


def create_charuco_board(config: dict[str, Any]) -> tuple[Any, Any]:
    import cv2

    board_config = config["board"]
    if board_config["type"].lower() != "charuco":
        raise ValueError("Only ChArUco boards are supported.")

    aruco_dictionary = get_aruco_dictionary(board_config["aruco_dictionary"])
    board = cv2.aruco.CharucoBoard(
        (board_config["squares_x"], board_config["squares_y"]),
        board_config["square_size_m"],
        board_config["marker_size_m"],
        aruco_dictionary,
    )
    return board, aruco_dictionary


def detect_charuco_frame(
    image: np.ndarray,
    board: Any,
    aruco_dictionary: Any,
) -> tuple[dict[str, Any], np.ndarray | None, np.ndarray | None, list[np.ndarray], np.ndarray | None]:
    import cv2

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    image_height, image_width = gray.shape[:2]

    detector_params = cv2.aruco.DetectorParameters()
    if hasattr(cv2.aruco, "ArucoDetector"):
        detector = cv2.aruco.ArucoDetector(aruco_dictionary, detector_params)
        marker_corners, marker_ids, _ = detector.detectMarkers(gray)
    else:
        marker_corners, marker_ids, _ = cv2.aruco.detectMarkers(
            gray,
            aruco_dictionary,
            parameters=detector_params,
        )

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
        "status": status,
        "image_width": image_width,
        "image_height": image_height,
        "marker_count": marker_count,
        "charuco_corner_count": charuco_corner_count,
        "used_for_calibration": used_for_calibration,
    }
    return detection, charuco_corners, charuco_ids, marker_corners, marker_ids


def build_detection_summary(
    config: dict[str, Any],
    source_mode: str,
    detections: list[dict[str, Any]],
) -> dict[str, Any]:
    valid_frames = [item for item in detections if item["used_for_calibration"]]
    min_required = int(config["calibration"]["min_image_count"])
    return {
        "source_mode": source_mode,
        "board_type": config["board"]["type"],
        "aruco_dictionary": config["board"].get("aruco_dictionary"),
        "detected_image_count": len(valid_frames),
        "failed_image_count": len(detections) - len(valid_frames),
        "min_required_images": min_required,
        "ready_for_calibration": len(valid_frames) >= min_required,
        "images": detections,
    }


def build_calibration_flags(config: dict[str, Any]) -> int:
    import cv2

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
    import cv2
    import numpy as np

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
    import cv2

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
    source_mode: str,
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
        "camera_name": config.get("camera_name", "default_camera"),
        "source_mode": source_mode,
        "image_width": image_width,
        "image_height": image_height,
        "intrinsics": asdict(intrinsics) if intrinsics else asdict(Intrinsics(None, None, None, None)),
        "distortion_model": config.get("distortion_model", "opencv_rational"),
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
            "todo": "Use the same resolution for runtime capture, undistortion, and Unity projection.",
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


def calibrate_and_write(
    config: dict[str, Any],
    source_mode: str,
    detections: list[dict[str, Any]],
    observations: list[CharucoObservation],
    board: Any,
    image_size: tuple[int, int] | None,
) -> tuple[Path, dict[str, Any], dict[str, Any]]:
    all_charuco_corners = [observation.corners for observation in observations]
    all_charuco_ids = [observation.ids for observation in observations]
    detection_summary = build_detection_summary(config, source_mode, detections)

    calibration_result = run_charuco_calibration(
        config=config,
        detection_summary=detection_summary,
        all_charuco_corners=all_charuco_corners,
        all_charuco_ids=all_charuco_ids,
        board=board,
        image_size=image_size,
    )

    image_width = image_size[0] if image_size is not None else None
    image_height = image_size[1] if image_size is not None else None
    payload = build_output_payload(
        config=config,
        source_mode=source_mode,
        image_width=image_width,
        image_height=image_height,
        image_count=len(detections),
        detection_summary=detection_summary,
        calibration_result=calibration_result,
    )
    output_path = write_output_json(config, payload)
    return output_path, payload, calibration_result


def print_calibration_report(
    config_path: Path,
    source_label: str,
    image_size: tuple[int, int] | None,
    detection_summary: dict[str, Any],
    calibration_result: dict[str, Any],
    output_path: Path,
) -> None:
    image_size_text = "unavailable"
    if image_size is not None:
        image_size_text = f"{image_size[0]}x{image_size[1]}"

    print(f"Loaded config: {config_path}")
    print(f"Calibration source: {source_label}")
    print(f"Inferred image size: {image_size_text}")
    print(
        "Detected usable ChArUco frames: "
        f"{detection_summary['detected_image_count']}/{len(detection_summary['images'])}"
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


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "--config",
        type=Path,
        default=DEFAULT_CONFIG_PATH,
        help="Path to the YAML calibration config.",
    )
    parser.add_argument(
        "--output-json",
        type=str,
        default=None,
        help="Override the output JSON file name from the YAML config.",
    )


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="LensLab live ChArUco calibration entrypoint."
    )
    subparsers = parser.add_subparsers(dest="mode")

    live_parser = subparsers.add_parser(
        "live",
        help="Calibrate by collecting ChArUco observations from a WebCam.",
    )
    add_common_arguments(live_parser)
    live_parser.add_argument("--camera-index", type=int, default=0, help="OpenCV camera index.")
    live_parser.add_argument("--width", type=int, default=1920, help="Requested capture width.")
    live_parser.add_argument("--height", type=int, default=1080, help="Requested capture height.")
    live_parser.add_argument(
        "--min-corners",
        type=int,
        default=8,
        help="Minimum ChArUco corners required before accepting a live sample.",
    )
    live_parser.add_argument(
        "--auto-capture-interval",
        type=float,
        default=0.0,
        help="Automatically accept a valid sample every N seconds; 0 means manual spacebar capture.",
    )
    live_parser.add_argument(
        "--save-frames",
        action="store_true",
        help="Save accepted live frames for later debugging.",
    )
    live_parser.add_argument(
        "--capture-dir",
        type=Path,
        default=Path("calibration/output/live_capture"),
        help="Directory for accepted live frames when --save-frames is enabled.",
    )
    live_parser.add_argument(
        "--window-name",
        type=str,
        default="LensLab Live Calibration",
        help="OpenCV preview window title.",
    )
    return parser


def normalize_argv(argv: Sequence[str] | None) -> list[str]:
    args = list(sys.argv[1:] if argv is None else argv)
    if not args:
        return ["live"]
    if args[0] not in {"live", "-h", "--help"}:
        return ["live", *args]
    return args


def main(argv: Sequence[str] | None = None) -> None:
    parser = build_arg_parser()
    args = parser.parse_args(normalize_argv(argv))

    if args.mode in {None, "live"}:
        if __package__:
            from .live import run_live_mode
        else:
            from live import run_live_mode

        run_live_mode(args)
        return

    parser.error(f"Unsupported mode: {args.mode}")


if __name__ == "__main__":
    main()
