# LensLab

LensLab is a Unity-facing computer vision project for camera calibration, GPU undistortion, pose estimation, and virtual camera matching.

This repository is being developed as a practical vertical slice rather than a set of disconnected experiments. The current milestone delivers a complete offline validation chain from Python/OpenCV into Unity.

## Current Milestone Status

The project has completed the following stage:

1. ChArUco-based camera calibration in Python/OpenCV
2. Shared calibration JSON export
3. Unity-side calibration loading
4. GPU undistortion validation against a CPU reference
5. Calibrated Unity camera projection mapping
6. Single-image ChArUco pose estimation in Python
7. Unity pose loading and board-region validation against the reference image

At the end of this milestone, the system can:

- calibrate a camera from ChArUco images
- export intrinsics and distortion coefficients to JSON
- undistort images in both Python and Unity
- map calibrated intrinsics onto a Unity Camera
- estimate board pose from a known ChArUco target
- render a Unity-side board region back onto the target image for validation

This means the main offline integration path is working end-to-end.

## What Has Been Validated

### Python / OpenCV

- ChArUco detection and frame filtering
- camera calibration with reprojection statistics
- undistortion preview generation
- single-image pose estimation with `solvePnP`
- shared JSON export for calibration and pose

### Unity

- loading calibration JSON from `Resources`
- compute-shader GPU undistortion
- CPU vs GPU undistortion comparison
- calibrated projection matrix application to `Camera.projectionMatrix`
- loading pose JSON from `Resources`
- rendering a board-aligned validation surface over the reference image

## Repository Structure

```text
LensLab/
+-- calibration/
|   +-- configs/
|   |   \-- charuco_board.yaml
|   +-- output/
|   |   +-- camera_calibration.json
|   |   +-- pose_estimation/
|   |   \-- undistort_preview/
|   \-- scripts/
|       +-- calibrate_camera.py
|       +-- estimate_pose.py
|       \-- undistort_preview.py
+-- data/
|   +-- calibration_images/
|   \-- samples/
+-- docs/
|   +-- architecture/
|   +-- notes/
|   \-- reports/
+-- native_plugin/
+-- shaders/
+-- unity_project/
|   \-- LensLab/
|       \-- Assets/
|           +-- Plugins/
|           |   \-- LensLab/
|           |       +-- Resources/
|           |       +-- Runtime/
|           |       \-- Shaders/
|           \-- Scenes/
\-- README.md
```

## Key Runtime Files

### Python

- `calibration/scripts/calibrate_camera.py`
  builds calibration results from ChArUco images
- `calibration/scripts/undistort_preview.py`
  exports CPU undistortion previews and Unity reference images
- `calibration/scripts/estimate_pose.py`
  estimates pose and exports pose JSON with board-model data

### Unity Runtime

- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabCalibrationLoader.cs`
- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabUndistortionController.cs`
- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabCameraProjectionController.cs`
- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabPoseLoader.cs`
- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabProjectionValidationOverlay.cs`
- `unity_project/LensLab/Assets/Plugins/LensLab/Runtime/LensLabPoseBoardDebug.cs`

### Unity Resources

- `unity_project/LensLab/Assets/Plugins/LensLab/Resources/LensLab/lenslab_calibration.json`
- `unity_project/LensLab/Assets/Plugins/LensLab/Resources/LensLab/pose_003.json`
- `unity_project/LensLab/Assets/Plugins/LensLab/Resources/LensLab/References/pose_reference_003.png`

## Shared Data Contracts

### Calibration JSON

Primary file:

- `calibration/output/camera_calibration.json`

Contains:

- image resolution
- `fx`, `fy`, `cx`, `cy`
- OpenCV rational distortion coefficients
- calibration target metadata
- reprojection summary

### Pose JSON

Primary file:

- `calibration/output/pose_estimation/003_pose.json`

Contains:

- `rvec` / `tvec`
- pose reprojection error
- board-model information exported from OpenCV
- reference image metadata

## Current Recommended Unity Validation Setup

For the cleanest stage-end validation scene, keep only these scene objects:

### `Main Camera`

Attach:

- `LensLabCameraProjectionController`
- `LensLabProjectionValidationOverlay`
- `LensLabPoseBoardDebug`

Recommended references:

- `LensLabCameraProjectionController.Calibration Loader` -> `LensLabBootstrap`
- `LensLabProjectionValidationOverlay.Calibration Loader` -> `LensLabBootstrap`
- `LensLabPoseBoardDebug.Pose Loader` -> `LensLabBootstrap`
- `LensLabPoseBoardDebug.Validation Overlay` -> `Main Camera`
- `LensLabPoseBoardDebug.Target Camera` -> `Main Camera`

### `LensLabBootstrap`

Attach:

- `LensLabCalibrationLoader`
- `LensLabPoseLoader`

### `Directional Light`

- default settings are fine

For this stage, legacy helper objects such as the old three-panel undistortion preview UI or projection alignment gizmos are not required in the scene.

## Stage-End Interpretation

The current stage should be interpreted as an offline integration milestone, not a finished runtime plugin.

What is complete:

- the core math/data path from OpenCV to Unity
- JSON contracts for calibration and pose
- GPU undistortion correctness against a CPU reference
- calibrated projection mapping in Unity
- pose-driven board-region validation

What is not yet complete:

- live camera ingestion
- real-time pose tracking inside Unity
- pose smoothing / jitter handling
- final virtual camera driving workflow
- packaging as a polished production plugin

## Next Stage

The next stage will focus on runtime-facing content anchoring and reporting:

- replace the debug board overlay with virtual content anchored on the detected board
- formalize the professor-facing progress report
- prepare a cleaner demo scene and milestone documentation

## Decisions Log

### 2026-03-29

Locked project decisions:

1. Build a complete vertical slice before optimization work.
2. Use JSON as the first shared interchange format.
3. Treat Python/OpenCV as the source of truth for calibration math.
4. Centralize OpenCV-to-Unity conversion rather than duplicating it.
5. Start GPU undistortion with a direct compute-shader implementation.

### 2026-04-10

Milestone closure decisions:

1. The current phase ends after offline pose-to-Unity board validation is working.
2. `pose_reference_003.png` is the canonical visual validation image for this milestone.
3. Scene validation for this phase should be reduced to the smallest useful setup.
4. Legacy door-frame and multi-panel comparison helpers are no longer the primary validation path.

## Environment

Recommended baseline:

- Windows
- Unity 2022.3+
- Python 3.10
- `opencv-contrib-python`
- Direct3D 11 capable GPU

Suggested Python packages:

```text
numpy
scipy
matplotlib
pyyaml
jupyter
opencv-contrib-python
```

## Author

Julie Yang  
Computer Science  
CSE 559a Computer Vision

## License

MIT License
