from __future__ import annotations

import sys
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
CALIBRATION_SCRIPT_DIR = SCRIPT_DIR / "calibration"
sys.path.insert(0, str(CALIBRATION_SCRIPT_DIR))

from calibrate import main  # noqa: E402


if __name__ == "__main__":
    main(["offline", *sys.argv[1:]])
