from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Any

import cv2

try:
    from .calibrate import (
        CharucoObservation,
        calibrate_and_write,
        create_charuco_board,
        detect_charuco_frame,
        load_yaml,
        print_calibration_report,
        repo_display_path,
        resolve_repo_path,
    )
except ImportError:
    from calibrate import (
        CharucoObservation,
        calibrate_and_write,
        create_charuco_board,
        detect_charuco_frame,
        load_yaml,
        print_calibration_report,
        repo_display_path,
        resolve_repo_path,
    )


def apply_offline_overrides(config: dict[str, Any], args: argparse.Namespace) -> None:
    if args.image_dir is not None:
        config["input"]["image_dir"] = str(args.image_dir)
    if args.image_glob is not None:
        config["input"]["image_glob"] = args.image_glob
    if args.output_json is not None:
        config["output"]["calibration_json"] = args.output_json


def collect_image_paths(config: dict[str, Any]) -> list[Path]:
    input_config = config["input"]
    image_dir = resolve_repo_path(input_config["image_dir"])
    image_glob = input_config["image_glob"]
    return sorted(path for path in image_dir.glob(image_glob) if path.is_file())


def infer_image_size(image_paths: list[Path]) -> tuple[int, int] | None:
    if not image_paths:
        return None

    image = cv2.imread(str(image_paths[0]), cv2.IMREAD_COLOR)
    if image is None:
        raise RuntimeError(f"Failed to load image: {image_paths[0]}")

    height, width = image.shape[:2]
    return width, height


def detect_image_dataset(
    image_paths: list[Path],
    board: Any,
    aruco_dictionary: Any,
) -> tuple[list[dict[str, Any]], list[CharucoObservation]]:
    detections: list[dict[str, Any]] = []
    observations: list[CharucoObservation] = []

    for image_index, image_path in enumerate(image_paths):
        image = cv2.imread(str(image_path), cv2.IMREAD_COLOR)
        if image is None:
            detections.append(
                {
                    "image_path": repo_display_path(image_path),
                    "status": "load_failed",
                    "image_width": None,
                    "image_height": None,
                    "marker_count": 0,
                    "charuco_corner_count": 0,
                    "used_for_calibration": False,
                }
            )
            continue

        detection, charuco_corners, charuco_ids, _, _ = detect_charuco_frame(
            image,
            board,
            aruco_dictionary,
        )
        detection["image_index"] = image_index
        detection["image_path"] = repo_display_path(image_path)
        detections.append(detection)

        if detection["used_for_calibration"] and charuco_corners is not None and charuco_ids is not None:
            observations.append(
                CharucoObservation(
                    corners=charuco_corners,
                    ids=charuco_ids,
                    metadata=detection,
                )
            )

    return detections, observations


def run_offline_mode(args: argparse.Namespace) -> None:
    config = load_yaml(args.config)
    apply_offline_overrides(config, args)
    image_paths = collect_image_paths(config)
    image_size = infer_image_size(image_paths)
    board, aruco_dictionary = create_charuco_board(config)
    detections, observations = detect_image_dataset(image_paths, board, aruco_dictionary)

    output_path, payload, calibration_result = calibrate_and_write(
        config=config,
        source_mode="offline_images",
        detections=detections,
        observations=observations,
        board=board,
        image_size=image_size,
    )

    print(f"Discovered images: {len(image_paths)}")
    print_calibration_report(
        config_path=args.config,
        source_label=str(resolve_repo_path(config["input"]["image_dir"])),
        image_size=image_size,
        detection_summary=payload["detection_summary"],
        calibration_result=calibration_result,
        output_path=output_path,
    )


if __name__ == "__main__":
    try:
        from .calibrate import main
    except ImportError:
        from calibrate import main

    main(["offline", *sys.argv[1:]])
