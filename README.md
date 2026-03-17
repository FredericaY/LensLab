# LensLab

LensLab is a Unity-based computer vision tool for **camera calibration,
real-time GPU lens undistortion, and virtual camera matching**.

The project bridges classical computer vision algorithms and real-time
graphics pipelines by integrating OpenCV calibration with Unity
rendering workflows. It provides an engine-level plugin that enables
accurate alignment between real camera images and virtual content.

LensLab is designed for applications such as:

-   Virtual production
-   AR preview
-   Mixed reality visualization
-   Camera-space debugging in game engines

------------------------------------------------------------------------

# Features

## Camera Calibration

LensLab performs camera calibration using a set of calibration-board
images.

The calibration stage estimates:

-   Camera intrinsic matrix
-   Lens distortion coefficients
-   Reprojection error statistics

Calibration is implemented using OpenCV's standard camera calibration
pipeline.

------------------------------------------------------------------------

## Real-Time GPU Lens Undistortion

After calibration, LensLab performs real-time undistortion of incoming
video frames.

Instead of applying the correction on the CPU, the project implements
GPU-based remapping using a compute shader.

Pipeline:

    camera frame
        ↓
    UV remapping
        ↓
    compute shader
        ↓
    undistorted frame

This allows real-time processing inside the Unity rendering pipeline.

------------------------------------------------------------------------

## Virtual Camera Matching

LensLab can estimate camera pose using a known planar calibration board
or marker pattern.

The system detects board corners or markers and solves the camera pose
using a Perspective-n-Point formulation.

The estimated pose is applied to a Unity virtual camera or virtual
object, enabling correct alignment between the real scene and virtual
content.

------------------------------------------------------------------------

# System Architecture

The system combines computer vision, graphics programming, and engine
integration.

    Calibration images
            ↓
    OpenCV camera calibration
            ↓
    intrinsics + distortion parameters
            ↓
    Unity plugin loads parameters
            ↓
    GPU undistortion (compute shader)
            ↓
    marker detection + solvePnP
            ↓
    virtual camera alignment

------------------------------------------------------------------------

# Repository Structure

    LensLab
    │
    ├── calibration/
    │   Python notebooks and scripts for camera calibration
    │
    ├── unity_project/
    │   Unity project containing the LensLab plugin and demo scene
    │
    ├── native_plugin/
    │   C++ native plugin for GPU processing
    │
    ├── shaders/
    │   HLSL compute shaders for undistortion remapping
    │
    ├── data/
    │   example calibration images
    │
    ├── docs/
    │   project report and technical documentation
    │
    └── README.md

------------------------------------------------------------------------

# Installation

## Requirements

-   Windows
-   Unity 2022 or later
-   Python 3.9+
-   OpenCV
-   Visual Studio (for native plugin compilation)
-   GPU supporting Direct3D 11

------------------------------------------------------------------------

## Clone the Repository

    git clone https://github.com/FredericaY/LensLab.git

------------------------------------------------------------------------

## Install Python Dependencies

    pip install opencv-python numpy matplotlib

------------------------------------------------------------------------

## Open the Unity Project

Open the `unity_project` folder in Unity Hub.

------------------------------------------------------------------------

# Usage

## Step 1: Collect Calibration Images

Print a checkerboard or ChArUco board and capture **15--30 images** from
different viewpoints.

------------------------------------------------------------------------

## Step 2: Run Calibration

Run the calibration script:

    python calibration/calibrate_camera.py

This will produce:

-   camera matrix
-   distortion coefficients
-   reprojection error report

------------------------------------------------------------------------

## Step 3: Import Parameters into Unity

Load the generated parameters into the LensLab plugin.

------------------------------------------------------------------------

## Step 4: Run Real-Time Undistortion

Start the Unity demo scene.

The system will:

-   capture camera frames
-   apply GPU undistortion
-   display original and corrected images

------------------------------------------------------------------------

## Step 5: Pose Estimation

When a calibration board or marker is visible, the system estimates
camera pose and aligns virtual objects with the real scene.

------------------------------------------------------------------------

# Evaluation

The system is evaluated using several metrics.

### Calibration Accuracy

Measured using reprojection error.

### Undistortion Quality

Visual comparison between distorted and corrected images.

### Runtime Performance

Frame processing time measured on GPU.

Typical results:

  Resolution   GPU Time
  ------------ -----------
  720p         \~1--2 ms
  1080p        \~2--4 ms

------------------------------------------------------------------------

# Dependencies

LensLab builds on several widely used libraries:

-   OpenCV
-   Unity Engine
-   Direct3D 11
-   HLSL compute shaders

------------------------------------------------------------------------

# Motivation

LensLab was developed as a computer vision course project focused on
integrating classical vision algorithms with real-time graphics engine
workflows.

Instead of focusing purely on model training, the project demonstrates
how computer vision techniques can be integrated into real engine tools
used in interactive graphics systems.

------------------------------------------------------------------------

# Author

Julie Yang\
Computer Science\
CSE 559a Computer Vision

------------------------------------------------------------------------

# License

MIT License
