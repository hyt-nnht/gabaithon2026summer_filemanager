"""Visual Studio debug entry point for the local analysis service."""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parent
SOURCE_DIR = PROJECT_DIR / "src"
if str(SOURCE_DIR) not in sys.path:
    sys.path.insert(0, str(SOURCE_DIR))

from file_analyzer.__main__ import main  # noqa: E402


if __name__ == "__main__":
    main()
