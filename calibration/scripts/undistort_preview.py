from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import cv2
import numpy as np


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CALIBRATION_PATH = REPO_ROOT / "calibration" / "output" / "camera_calibration.json"
DEFAULT_OUTPUT_DIR = REPO_ROOT / "calibration" / "output" / "undistort_preview"
DEFAULT_UNITY_REFERENCE_DIR = (
    REPO_ROOT
    / "unity_project"
    / "LensLab"
    / "Assets"
    / "Plugins"
    / "LensLab"
    / "Resources"
    / "LensLab"
    / "References"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a CPU undistortion preview from a LensLab calibration JSON."
    )
    parser.add_argument(
        "--calibration",
        type=Path,
        default=DEFAULT_CALIBRATION_PATH,
        help="Path to the calibration JSON file.",
    )
    parser.add_argument(
        "--image",
        type=Path,
        required=True,
        help="Path to the source image to undistort.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=DEFAULT_OUTPUT_DIR,
        help="Directory for preview output images.",
    )
    parser.add_argument(
        "--alpha",
        type=float,
        default=0.0,
        help="Free scaling parameter for getOptimalNewCameraMatrix in [0, 1].",
    )
    parser.add_argument(
        "--export-unity-reference",
        action="store_true",
        help="Also export the undistorted image into the Unity Resources folder for CPU/GPU comparison.",
    )
    parser.add_argument(
        "--unity-reference-dir",
        type=Path,
        default=DEFAULT_UNITY_REFERENCE_DIR,
        help="Unity Resources directory used for CPU reference image export.",
    )
    parser.add_argument(
        "--unity-reference-name",
        type=str,
        default="cpu_reference",
        help="Base file name for the Unity CPU reference export, without extension.",
    )
    return parser.parse_args()


def resolve_repo_path(path: Path) -> Path:
    if path.is_absolute():
        return path
    return REPO_ROOT / path


def load_calibration(path: Path) -> dict[str, Any]:
    resolved_path = resolve_repo_path(path)
    with resolved_path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def build_camera_matrix(calibration: dict[str, Any]) -> np.ndarray:
    intrinsics = calibration["intrinsics"]
    return np.array(
        [
            [intrinsics["fx"], 0.0, intrinsics["cx"]],
            [0.0, intrinsics["fy"], intrinsics["cy"]],
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
    sanitized = [0.0 if value is None else float(value) for value in ordered]
    return np.array(sanitized, dtype=np.float64)


def create_side_by_side(left: np.ndarray, right: np.ndarray) -> np.ndarray:
    if left.shape != right.shape:
        raise ValueError("Input images for comparison must have the same shape.")

    height, width = left.shape[:2]
    canvas = np.zeros((height, width * 2, 3), dtype=np.uint8)
    canvas[:, :width] = left
    canvas[:, width:] = right

    cv2.putText(canvas, "Original", (40, 70), cv2.FONT_HERSHEY_SIMPLEX, 2.0, (0, 255, 0), 4, cv2.LINE_AA)
    cv2.putText(canvas, "CPU Reference", (width + 40, 70), cv2.FONT_HERSHEY_SIMPLEX, 2.0, (0, 255, 0), 4, cv2.LINE_AA)
    cv2.line(canvas, (width, 0), (width, height), (255, 255, 255), 3, cv2.LINE_AA)
    return canvas


def undistort_image(
    image: np.ndarray,
    camera_matrix: np.ndarray,
    dist_coeffs: np.ndarray,
    alpha: float,
) -> tuple[np.ndarray, np.ndarray, tuple[int, int, int, int]]:
    height, width = image.shape[:2]
    new_camera_matrix, roi = cv2.getOptimalNewCameraMatrix(
        camera_matrix,
        dist_coeffs,
        (width, height),
        alpha,
        (width, height),
    )
    undistorted = cv2.undistort(image, camera_matrix, dist_coeffs, None, new_camera_matrix)
    return undistorted, new_camera_matrix, roi


def export_unity_reference(path: Path, image: np.ndarray) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(path), image)
    return path


def main() -> None:
    args = parse_args()
    calibration = load_calibration(args.calibration)
    image_path = resolve_repo_path(args.image)
    output_dir = resolve_repo_path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError(f"Failed to load image: {image_path}")

    camera_matrix = build_camera_matrix(calibration)
    dist_coeffs = build_distortion_vector(calibration)
    undistorted, new_camera_matrix, roi = undistort_image(
        image=image,
        camera_matrix=camera_matrix,
        dist_coeffs=dist_coeffs,
        alpha=args.alpha,
    )
    comparison = create_side_by_side(image, undistorted)

    stem = image_path.stem
    undistorted_path = output_dir / f"{stem}_undistorted.png"
    comparison_path = output_dir / f"{stem}_comparison.png"

    cv2.imwrite(str(undistorted_path), undistorted)
    cv2.imwrite(str(comparison_path), comparison)

    unity_reference_path = None
    if args.export_unity_reference:
        unity_reference_dir = resolve_repo_path(args.unity_reference_dir)
        unity_reference_path = unity_reference_dir / f"{args.unity_reference_name}.png"
        export_unity_reference(unity_reference_path, undistorted)

    print(f"Calibration file: {resolve_repo_path(args.calibration)}")
    print(f"Source image: {image_path}")
    print(f"Output directory: {output_dir}")
    print(f"ROI: {roi}")
    print("New camera matrix:")
    print(new_camera_matrix)
    print(f"Wrote undistorted image: {undistorted_path}")
    print(f"Wrote comparison image: {comparison_path}")
    if unity_reference_path is not None:
        print(f"Wrote Unity CPU reference: {unity_reference_path}")
        print("Unity Resources path: LensLab/References/" + args.unity_reference_name)


if __name__ == "__main__":
    main()
