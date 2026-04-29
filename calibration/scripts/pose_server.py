from __future__ import annotations

import argparse
import json
import socket
import struct
import sys
import threading
from pathlib import Path
from typing import Any

import cv2
import numpy as np

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG_PATH = REPO_ROOT / "calibration" / "configs" / "charuco_board.yaml"
DEFAULT_CALIBRATION_PATH = REPO_ROOT / "calibration" / "output" / "camera_calibration.json"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 5555


# ---------------------------------------------------------------------------
# Config / calibration helpers
# ---------------------------------------------------------------------------

def load_yaml(path: Path) -> dict[str, Any]:
    import yaml
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
        raise ValueError("Only ChArUco boards are supported.")
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
        coeffs.get("k1", 0.0), coeffs.get("k2", 0.0),
        coeffs.get("p1", 0.0), coeffs.get("p2", 0.0),
        coeffs.get("k3", 0.0), coeffs.get("k4", 0.0),
        coeffs.get("k5", 0.0), coeffs.get("k6", 0.0),
    ]
    return np.array([0.0 if v is None else float(v) for v in ordered], dtype=np.float64)


# ---------------------------------------------------------------------------
# Detection and pose estimation
# ---------------------------------------------------------------------------

def detect_and_estimate_pose(
    frame: np.ndarray,
    board: Any,
    aruco_dictionary: Any,
    camera_matrix: np.ndarray,
    dist_coeffs: np.ndarray,
    min_corners: int,
) -> dict[str, Any]:
    """Run ChArUco detection and solvePnP on a single BGR frame.

    Returns a flat dict suitable for JSON serialisation to Unity.
    rotation_matrix_flat is a row-major 9-element list (3x3 matrix).
    """
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    detector = cv2.aruco.ArucoDetector(aruco_dictionary, cv2.aruco.DetectorParameters())
    marker_corners, marker_ids, _ = detector.detectMarkers(gray)

    marker_count = 0 if marker_ids is None else int(len(marker_ids))

    if marker_count == 0:
        return {"detected": False, "marker_count": 0, "charuco_corner_count": 0}

    _, charuco_corners, charuco_ids = cv2.aruco.interpolateCornersCharuco(
        marker_corners, marker_ids, gray, board
    )
    charuco_count = 0 if charuco_ids is None else int(len(charuco_ids))

    if charuco_corners is None or charuco_ids is None or charuco_count < min_corners:
        return {"detected": False, "marker_count": marker_count, "charuco_corner_count": charuco_count}

    chessboard_corners = board.getChessboardCorners()
    object_points = chessboard_corners[charuco_ids.flatten()].reshape(-1, 1, 3)
    image_points = charuco_corners.reshape(-1, 1, 2)

    success, rvec, tvec = cv2.solvePnP(
        object_points, image_points, camera_matrix, dist_coeffs,
        flags=cv2.SOLVEPNP_ITERATIVE,
    )

    if not success or rvec is None or tvec is None:
        return {"detected": False, "marker_count": marker_count, "charuco_corner_count": charuco_count}

    # Reprojection error
    projected, _ = cv2.projectPoints(object_points, rvec, tvec, camera_matrix, dist_coeffs)
    errors = np.linalg.norm(image_points.reshape(-1, 2) - projected.reshape(-1, 2), axis=1)
    mean_error = float(errors.mean()) if len(errors) > 0 else 0.0

    rotation_matrix, _ = cv2.Rodrigues(rvec)
    rotation_matrix_flat = [float(v) for v in rotation_matrix.flatten()]

    return {
        "detected": True,
        "marker_count": marker_count,
        "charuco_corner_count": charuco_count,
        "rvec": [float(x) for x in rvec.reshape(-1)],
        "tvec": [float(x) for x in tvec.reshape(-1)],
        "rotation_matrix_flat": rotation_matrix_flat,
        "reprojection_error": mean_error,
    }


# ---------------------------------------------------------------------------
# TCP protocol helpers
# (length-prefixed framing: 4-byte little-endian uint32 + payload)
# ---------------------------------------------------------------------------

def recv_exact(sock: socket.socket, length: int) -> bytes:
    data = bytearray()
    while len(data) < length:
        chunk = sock.recv(length - len(data))
        if not chunk:
            raise ConnectionError("Connection closed while reading data.")
        data.extend(chunk)
    return bytes(data)


def send_response(sock: socket.socket, payload: dict[str, Any]) -> None:
    json_bytes = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    header = struct.pack("<I", len(json_bytes))
    sock.sendall(header + json_bytes)


# ---------------------------------------------------------------------------
# Per-connection handler
# ---------------------------------------------------------------------------

_JPEG_SIZE_LIMIT = 20 * 1024 * 1024  # 20 MB sanity cap


def handle_client(
    conn: socket.socket,
    addr: tuple,
    board: Any,
    aruco_dictionary: Any,
    camera_matrix: np.ndarray,
    dist_coeffs: np.ndarray,
    min_corners: int,
    verbose: bool,
) -> None:
    if verbose:
        print(f"[PoseServer] Client connected: {addr}")

    try:
        while True:
            header = recv_exact(conn, 4)
            (jpeg_length,) = struct.unpack("<I", header)

            # Heartbeat: Unity sends a 0-length frame to check connectivity.
            if jpeg_length == 0:
                send_response(conn, {"detected": False, "marker_count": 0, "charuco_corner_count": 0})
                continue

            if jpeg_length > _JPEG_SIZE_LIMIT:
                raise ValueError(f"Received implausibly large frame: {jpeg_length} bytes")

            jpeg_bytes = recv_exact(conn, jpeg_length)

            nparr = np.frombuffer(jpeg_bytes, dtype=np.uint8)
            frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
            if frame is None:
                send_response(conn, {
                    "detected": False, "marker_count": 0,
                    "charuco_corner_count": 0, "error": "decode_failed",
                })
                continue

            result = detect_and_estimate_pose(
                frame, board, aruco_dictionary, camera_matrix, dist_coeffs, min_corners,
            )

            if verbose and result["detected"]:
                tvec = result["tvec"]
                print(
                    f"[PoseServer] Detected  corners={result['charuco_corner_count']:2d}"
                    f"  tx={tvec[0]:+.4f}  ty={tvec[1]:+.4f}  tz={tvec[2]:+.4f}"
                    f"  err={result['reprojection_error']:.3f}px"
                )

            send_response(conn, result)

    except (ConnectionError, ConnectionResetError, EOFError):
        pass
    except Exception as exc:
        print(f"[PoseServer] Error handling client {addr}: {exc}", file=sys.stderr)
    finally:
        conn.close()
        if verbose:
            print(f"[PoseServer] Client disconnected: {addr}")


# ---------------------------------------------------------------------------
# Server entry point
# ---------------------------------------------------------------------------

def run_server(args: argparse.Namespace) -> None:
    config = load_yaml(args.config)
    calibration = load_json(args.calibration)
    board, aruco_dictionary = create_charuco_board(config)
    camera_matrix = build_camera_matrix(calibration)
    dist_coeffs = build_distortion_vector(calibration)

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen(1)

    print(f"[PoseServer] Listening on {args.host}:{args.port}")
    print(f"[PoseServer] Config     : {args.config}")
    print(f"[PoseServer] Calibration: {args.calibration}")
    print(f"[PoseServer] Min corners: {args.min_corners}")
    print("[PoseServer] Waiting for Unity... (Ctrl+C to stop)")

    try:
        while True:
            conn, addr = server.accept()
            thread = threading.Thread(
                target=handle_client,
                args=(
                    conn, addr, board, aruco_dictionary,
                    camera_matrix, dist_coeffs,
                    args.min_corners, args.verbose,
                ),
                daemon=True,
            )
            thread.start()
    except KeyboardInterrupt:
        print("\n[PoseServer] Shutting down.")
    finally:
        server.close()


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="LensLab real-time ChArUco pose estimation server.\n"
                    "Receives JPEG frames from Unity over TCP, returns pose JSON.",
    )
    parser.add_argument("--host", type=str, default=DEFAULT_HOST,
                        help="TCP listen address (default: 127.0.0.1).")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help="TCP listen port (default: 5555).")
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG_PATH,
                        help="Path to charuco_board.yaml.")
    parser.add_argument("--calibration", type=Path, default=DEFAULT_CALIBRATION_PATH,
                        help="Path to camera_calibration.json.")
    parser.add_argument("--min-corners", type=int, default=4,
                        help="Minimum ChArUco corners required to estimate pose (default: 4).")
    parser.add_argument("--verbose", action="store_true", default=True,
                        help="Print pose results to console (default: on).")
    parser.add_argument("--no-verbose", dest="verbose", action="store_false",
                        help="Suppress per-frame console output.")
    return parser.parse_args()


if __name__ == "__main__":
    run_server(parse_args())
