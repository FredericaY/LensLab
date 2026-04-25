from __future__ import annotations

import argparse
import sys
import time
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


def apply_live_overrides(config: dict[str, Any], args: argparse.Namespace) -> None:
    if args.output_json is not None:
        config["output"]["calibration_json"] = args.output_json


def draw_status(
    frame: Any,
    detection: dict[str, Any],
    sample_count: int,
    min_required: int,
    min_corners: int,
) -> None:
    lines = [
        f"samples: {sample_count}/{min_required}",
        f"markers: {detection['marker_count']}  charuco: {detection['charuco_corner_count']}",
        "space: capture   c: calibrate   q/esc: quit",
    ]
    if detection["charuco_corner_count"] < min_corners:
        lines.append(f"need at least {min_corners} corners for capture")

    for index, line in enumerate(lines):
        y = 28 + index * 28
        cv2.putText(
            frame,
            line,
            (20, y),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.75,
            (20, 220, 20),
            2,
            cv2.LINE_AA,
        )


def save_live_frame(frame: Any, capture_dir: Path, sample_index: int) -> Path:
    capture_dir.mkdir(parents=True, exist_ok=True)
    output_path = capture_dir / f"live_{sample_index:03d}.png"
    cv2.imwrite(str(output_path), frame)
    return output_path


def append_live_sample(
    frame: Any,
    detection: dict[str, Any],
    charuco_corners: Any,
    charuco_ids: Any,
    detections: list[dict[str, Any]],
    observations: list[CharucoObservation],
    args: argparse.Namespace,
) -> bool:
    if charuco_corners is None or charuco_ids is None:
        return False
    if detection["charuco_corner_count"] < args.min_corners:
        return False

    sample_index = len(observations)
    sample_detection = dict(detection)
    sample_detection["sample_index"] = sample_index
    sample_detection["source"] = "webcam"

    if args.save_frames:
        capture_path = save_live_frame(
            frame,
            resolve_repo_path(args.capture_dir),
            sample_index,
        )
        sample_detection["image_path"] = repo_display_path(capture_path)
    else:
        sample_detection["image_path"] = None

    detections.append(sample_detection)
    observations.append(
        CharucoObservation(
            corners=charuco_corners.copy(),
            ids=charuco_ids.copy(),
            metadata=sample_detection,
        )
    )
    return True


def open_capture(args: argparse.Namespace) -> Any:
    backend = cv2.CAP_DSHOW if hasattr(cv2, "CAP_DSHOW") else 0
    cap = cv2.VideoCapture(args.camera_index, backend)
    if args.width > 0:
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    if args.height > 0:
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    return cap


def run_live_mode(args: argparse.Namespace) -> None:
    config = load_yaml(args.config)
    apply_live_overrides(config, args)
    board, aruco_dictionary = create_charuco_board(config)
    min_required = int(config["calibration"]["min_image_count"])

    cap = open_capture(args)
    if not cap.isOpened():
        raise RuntimeError(f"Failed to open camera index {args.camera_index}")

    detections: list[dict[str, Any]] = []
    observations: list[CharucoObservation] = []
    image_size: tuple[int, int] | None = None
    last_auto_capture = 0.0
    calibrated = False

    print("Live calibration controls:")
    print("  space: capture current valid ChArUco frame")
    print("  c: calibrate from captured frames")
    print("  q or esc: quit without writing a new calibration")

    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                raise RuntimeError("Failed to read from the camera.")

            height, width = frame.shape[:2]
            image_size = (width, height)
            detection, charuco_corners, charuco_ids, marker_corners, marker_ids = detect_charuco_frame(
                frame,
                board,
                aruco_dictionary,
            )

            preview = frame.copy()
            if marker_ids is not None and len(marker_ids) > 0:
                cv2.aruco.drawDetectedMarkers(preview, marker_corners, marker_ids)
            if charuco_corners is not None and charuco_ids is not None:
                cv2.aruco.drawDetectedCornersCharuco(preview, charuco_corners, charuco_ids)
            draw_status(preview, detection, len(observations), min_required, args.min_corners)
            cv2.imshow(args.window_name, preview)

            now = time.monotonic()
            auto_capture_due = (
                args.auto_capture_interval > 0.0
                and now - last_auto_capture >= args.auto_capture_interval
            )
            if auto_capture_due:
                if append_live_sample(
                    frame,
                    detection,
                    charuco_corners,
                    charuco_ids,
                    detections,
                    observations,
                    args,
                ):
                    last_auto_capture = now
                    print(f"Captured sample {len(observations)}/{min_required}")

            key = cv2.waitKey(1) & 0xFF
            if key in {27, ord("q")}:
                break
            if key == ord(" "):
                if append_live_sample(
                    frame,
                    detection,
                    charuco_corners,
                    charuco_ids,
                    detections,
                    observations,
                    args,
                ):
                    print(f"Captured sample {len(observations)}/{min_required}")
                else:
                    print("Skipped frame: not enough ChArUco corners.")
            if key == ord("c"):
                calibrated = True
                break
    finally:
        cap.release()
        cv2.destroyAllWindows()

    if not calibrated:
        print("Live calibration cancelled; no calibration JSON was written.")
        return

    output_path, payload, calibration_result = calibrate_and_write(
        config=config,
        source_mode="live_webcam",
        detections=detections,
        observations=observations,
        board=board,
        image_size=image_size,
    )
    print_calibration_report(
        config_path=args.config,
        source_label=f"webcam index {args.camera_index}",
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

    main(["live", *sys.argv[1:]])
