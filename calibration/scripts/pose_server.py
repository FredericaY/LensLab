from __future__ import annotations

import argparse
import json
import socket
import struct
import sys
import threading
import time
from pathlib import Path
from typing import Any, Optional

import cv2
import numpy as np

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG_PATH = REPO_ROOT / "calibration" / "configs" / "charuco_board.yaml"
DEFAULT_CALIBRATION_PATH = REPO_ROOT / "calibration" / "output" / "camera_calibration.json"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 5555


# ---------------------------------------------------------------------------
# Architecture
# ---------------------------------------------------------------------------
# This server is a *publisher*: it owns the camera, runs detection, and pushes
# (jpeg_frame, pose_json) tuples to all connected Unity clients.
#
# Key design decisions:
#   * OpenCV opens the webcam directly. On Windows we force the MJPG fourcc so
#     the camera's onboard ISP delivers compressed frames over USB; this is what
#     lets us actually hit 30 fps at 1080p (Unity's WebCamTexture cannot do this
#     on Windows).
#   * One TCP connection per client. Each message sent to a client is:
#       [4B jpeg_len][jpeg_bytes][4B json_len][json_bytes]
#     All integers are little-endian uint32.
#   * Each client thread holds a single-slot "latest frame" mailbox. The capture
#     loop drops old frames if the client is slow, so Unity never accumulates
#     latency.
#
# Protocol (this file -> Unity):
#   MSG = U32_LE(jpeg_len) | jpeg_bytes | U32_LE(json_len) | json_bytes
#   pose JSON shape matches LensLabLivePoseData on the Unity side.
# ---------------------------------------------------------------------------


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


def scale_camera_matrix(
    camera_matrix: np.ndarray,
    calibration: dict[str, Any],
    frame_width: int,
    frame_height: int,
) -> np.ndarray:
    calibration_width = float(calibration.get("image_width", 0) or 0)
    calibration_height = float(calibration.get("image_height", 0) or 0)
    if calibration_width <= 0 or calibration_height <= 0:
        return camera_matrix

    scaled = camera_matrix.copy()
    scale_x = float(frame_width) / calibration_width
    scale_y = float(frame_height) / calibration_height
    scaled[0, 0] *= scale_x
    scaled[0, 2] *= scale_x
    scaled[1, 1] *= scale_y
    scaled[1, 2] *= scale_y
    return scaled


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
    detector: Any,
    camera_matrix: np.ndarray,
    dist_coeffs: np.ndarray,
    min_corners: int,
) -> dict[str, Any]:
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    marker_corners, marker_ids, _ = detector.detectMarkers(gray)
    marker_count = 0 if marker_ids is None else int(len(marker_ids))

    if marker_count == 0:
        return {"detected": False, "marker_count": 0, "charuco_corner_count": 0}

    _, charuco_corners, charuco_ids = cv2.aruco.interpolateCornersCharuco(
        marker_corners, marker_ids, gray, board
    )
    charuco_count = 0 if charuco_ids is None else int(len(charuco_ids))

    # cv2.SOLVEPNP_ITERATIVE uses DLT for initialization and needs at least
    # 6 3D-2D correspondences. Treat fewer corners as "not detected" instead
    # of letting OpenCV raise and kill the capture thread.
    required_corners = max(int(min_corners), 6)
    if charuco_corners is None or charuco_ids is None or charuco_count < required_corners:
        return {
            "detected": False,
            "marker_count": marker_count,
            "charuco_corner_count": charuco_count,
        }

    chessboard_corners = board.getChessboardCorners()
    object_points = chessboard_corners[charuco_ids.flatten()].reshape(-1, 1, 3)
    image_points = charuco_corners.reshape(-1, 1, 2)

    try:
        success, rvec, tvec = cv2.solvePnP(
            object_points, image_points, camera_matrix, dist_coeffs,
            flags=cv2.SOLVEPNP_ITERATIVE,
        )
    except cv2.error as exc:
        return {
            "detected": False,
            "marker_count": marker_count,
            "charuco_corner_count": charuco_count,
            "error": f"solvepnp_failed: {exc}",
        }
    if not success or rvec is None or tvec is None:
        return {
            "detected": False,
            "marker_count": marker_count,
            "charuco_corner_count": charuco_count,
        }

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
# Camera capture
# ---------------------------------------------------------------------------

def open_camera(
    index: int,
    width: int,
    height: int,
    fps: int,
    backend: str,
) -> cv2.VideoCapture:
    """Open the webcam, force MJPG, and request the desired resolution/fps.

    Forcing MJPG on Windows is critical: the default raw YUY2 negotiation caps
    most USB webcams to ~5 fps at 1080p. MJPG lets the camera ship compressed
    frames over USB and we typically reach the requested fps.
    """
    backend_id = {
        "auto":  cv2.CAP_ANY,
        "dshow": cv2.CAP_DSHOW,
        "msmf":  cv2.CAP_MSMF,
    }.get(backend.lower(), cv2.CAP_ANY)

    cap = cv2.VideoCapture(index, backend_id)
    if not cap.isOpened():
        raise RuntimeError(f"Failed to open camera index {index} (backend={backend}).")

    cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*"MJPG"))
    cap.set(cv2.CAP_PROP_FRAME_WIDTH,  width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
    cap.set(cv2.CAP_PROP_FPS,          fps)
    cap.set(cv2.CAP_PROP_BUFFERSIZE,   1)  # don't buffer stale frames

    return cap


# ---------------------------------------------------------------------------
# Per-client state: each client has a single-slot mailbox holding the latest
# (jpeg, json) message. The capture thread overwrites it; the sender thread
# drains it. This guarantees clients never lag behind the camera.
# ---------------------------------------------------------------------------

class ClientMailbox:
    __slots__ = ("_lock", "_event", "_payload", "_closed")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._event = threading.Event()
        self._payload: Optional[bytes] = None
        self._closed = False

    def put(self, payload: bytes) -> None:
        with self._lock:
            if self._closed:
                return
            self._payload = payload
            self._event.set()

    def get(self, timeout: float = 1.0) -> Optional[bytes]:
        if not self._event.wait(timeout):
            return None
        with self._lock:
            payload = self._payload
            self._payload = None
            self._event.clear()
            return payload

    def close(self) -> None:
        with self._lock:
            self._closed = True
            self._event.set()


_clients_lock = threading.Lock()
_clients: list[ClientMailbox] = []


def encode_message(jpeg_bytes: bytes, pose: dict[str, Any]) -> bytes:
    json_bytes = json.dumps(pose, separators=(",", ":")).encode("utf-8")
    return (
        struct.pack("<I", len(jpeg_bytes)) + jpeg_bytes
        + struct.pack("<I", len(json_bytes)) + json_bytes
    )


def broadcast_to_clients(payload: bytes) -> None:
    with _clients_lock:
        snapshot = list(_clients)
    for mailbox in snapshot:
        mailbox.put(payload)


# ---------------------------------------------------------------------------
# Per-connection sender thread
# ---------------------------------------------------------------------------

def handle_client(conn: socket.socket, addr: tuple, verbose: bool) -> None:
    if verbose:
        print(f"[PoseServer] Client connected: {addr}")

    mailbox = ClientMailbox()
    with _clients_lock:
        _clients.append(mailbox)

    try:
        while True:
            payload = mailbox.get(timeout=1.0)
            if payload is None:
                # Periodic wake-up; check the socket is still alive by sending
                # zero bytes (no-op) - if peer closed, sendall will raise.
                continue
            conn.sendall(payload)
    except (ConnectionError, ConnectionResetError, BrokenPipeError, OSError):
        pass
    except Exception as exc:
        print(f"[PoseServer] Sender error for {addr}: {exc}", file=sys.stderr)
    finally:
        mailbox.close()
        with _clients_lock:
            try:
                _clients.remove(mailbox)
            except ValueError:
                pass
        try:
            conn.close()
        except OSError:
            pass
        if verbose:
            print(f"[PoseServer] Client disconnected: {addr}")


# ---------------------------------------------------------------------------
# Capture + detection loop (single thread, owns the camera)
# ---------------------------------------------------------------------------

def capture_loop(
    cap: cv2.VideoCapture,
    board: Any,
    aruco_dictionary: Any,
    camera_matrix: np.ndarray,
    calibration: dict[str, Any],
    dist_coeffs: np.ndarray,
    min_corners: int,
    jpeg_quality: int,
    stop_event: threading.Event,
    verbose: bool,
) -> None:
    detector = cv2.aruco.ArucoDetector(aruco_dictionary, cv2.aruco.DetectorParameters())
    encode_params = [int(cv2.IMWRITE_JPEG_QUALITY), int(jpeg_quality)]

    actual_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    actual_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    actual_fps = cap.get(cv2.CAP_PROP_FPS)
    print(f"[PoseServer] Camera negotiated: {actual_w}x{actual_h} @ {actual_fps:.1f} fps")

    frame_camera_matrix: Optional[np.ndarray] = None
    matrix_size: tuple[int, int] = (0, 0)
    frame_count = 0
    last_log_time = time.time()
    last_log_count = 0

    while not stop_event.is_set():
        ok, frame = cap.read()
        if not ok or frame is None:
            time.sleep(0.005)
            continue

        frame_h, frame_w = frame.shape[:2]
        if matrix_size != (frame_w, frame_h):
            matrix_size = (frame_w, frame_h)
            frame_camera_matrix = scale_camera_matrix(camera_matrix, calibration, frame_w, frame_h)

        try:
            pose = detect_and_estimate_pose(
                frame, board, aruco_dictionary, detector,
                frame_camera_matrix, dist_coeffs, min_corners,
            )
        except Exception as exc:
            pose = {
                "detected": False,
                "marker_count": 0,
                "charuco_corner_count": 0,
                "error": f"detection_failed: {exc}",
            }

        # Encode JPEG
        ok, encoded = cv2.imencode(".jpg", frame, encode_params)
        if not ok:
            continue
        jpeg_bytes = encoded.tobytes()

        # Broadcast (only if at least one client is connected; saves work)
        with _clients_lock:
            has_clients = bool(_clients)
        if has_clients:
            broadcast_to_clients(encode_message(jpeg_bytes, pose))

        frame_count += 1
        if verbose:
            now = time.time()
            if now - last_log_time >= 2.0:
                fps = (frame_count - last_log_count) / (now - last_log_time)
                state = "DETECTED" if pose.get("detected") else "no board "
                tail = ""
                if pose.get("detected"):
                    tvec = pose.get("tvec", [0, 0, 0])
                    tail = (
                        f"  tx={tvec[0]:+.3f} ty={tvec[1]:+.3f} tz={tvec[2]:+.3f}"
                        f"  err={pose.get('reprojection_error', 0):.2f}px"
                    )
                with _clients_lock:
                    n_clients = len(_clients)
                print(
                    f"[PoseServer] {fps:5.1f} fps  clients={n_clients}  "
                    f"{state}  jpeg={len(jpeg_bytes)/1024:.1f}KB{tail}"
                )
                last_log_time = now
                last_log_count = frame_count

    print("[PoseServer] Capture loop stopped.")


# ---------------------------------------------------------------------------
# Server entry point
# ---------------------------------------------------------------------------

def run_server(args: argparse.Namespace) -> None:
    config = load_yaml(args.config)
    calibration = load_json(args.calibration)
    board, aruco_dictionary = create_charuco_board(config)
    camera_matrix = build_camera_matrix(calibration)
    dist_coeffs = build_distortion_vector(calibration)

    cap = open_camera(
        index=args.camera,
        width=args.width,
        height=args.height,
        fps=args.fps,
        backend=args.backend,
    )

    stop_event = threading.Event()
    capture_thread = threading.Thread(
        target=capture_loop,
        args=(
            cap, board, aruco_dictionary, camera_matrix, calibration, dist_coeffs,
            args.min_corners, args.jpeg_quality, stop_event, args.verbose,
        ),
        name="PoseServerCapture",
        daemon=True,
    )
    capture_thread.start()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen(4)

    print(f"[PoseServer] Listening on {args.host}:{args.port}")
    print(f"[PoseServer] Camera index: {args.camera}  backend={args.backend}")
    print(f"[PoseServer] Requested   : {args.width}x{args.height} @ {args.fps} fps  jpeg_q={args.jpeg_quality}")
    print(f"[PoseServer] Config      : {args.config}")
    print(f"[PoseServer] Calibration : {args.calibration}")
    print(f"[PoseServer] Min corners : {args.min_corners}")
    print("[PoseServer] Waiting for Unity... (Ctrl+C to stop)")

    try:
        while True:
            conn, addr = server.accept()
            # Disable Nagle so frames don't get coalesced + delayed
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            thread = threading.Thread(
                target=handle_client,
                args=(conn, addr, args.verbose),
                daemon=True,
            )
            thread.start()
    except KeyboardInterrupt:
        print("\n[PoseServer] Shutting down.")
    finally:
        stop_event.set()
        capture_thread.join(timeout=2.0)
        try:
            cap.release()
        except Exception:
            pass
        try:
            server.close()
        except Exception:
            pass


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "LensLab pose server (publisher mode).\n"
            "Owns the webcam and pushes (jpeg, pose JSON) to all connected "
            "Unity clients."
        ),
    )
    parser.add_argument("--host", type=str, default=DEFAULT_HOST,
                        help="TCP listen address (default: 127.0.0.1).")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help="TCP listen port (default: 5555).")
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG_PATH,
                        help="Path to charuco_board.yaml.")
    parser.add_argument("--calibration", type=Path, default=DEFAULT_CALIBRATION_PATH,
                        help="Path to camera_calibration.json.")
    parser.add_argument("--camera", type=int, default=0,
                        help="Camera device index (default: 0).")
    parser.add_argument("--backend", type=str, default="dshow",
                        choices=["auto", "dshow", "msmf"],
                        help="OpenCV capture backend on Windows (default: dshow).")
    parser.add_argument("--width", type=int, default=1920,
                        help="Requested capture width in pixels (default: 1920).")
    parser.add_argument("--height", type=int, default=1080,
                        help="Requested capture height in pixels (default: 1080).")
    parser.add_argument("--fps", type=int, default=30,
                        help="Requested capture frame rate (default: 30).")
    parser.add_argument("--jpeg-quality", type=int, default=75,
                        help="JPEG encode quality 0-100 (default: 75).")
    parser.add_argument("--min-corners", type=int, default=6,
                        help="Minimum ChArUco corners required to estimate pose (default: 6).")
    parser.add_argument("--verbose", action="store_true", default=True,
                        help="Print fps and pose summary every 2s (default: on).")
    parser.add_argument("--no-verbose", dest="verbose", action="store_false",
                        help="Suppress periodic console output.")
    return parser.parse_args()


if __name__ == "__main__":
    run_server(parse_args())
