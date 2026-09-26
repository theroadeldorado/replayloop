"""Fetch Google's MoveNet SinglePose model and convert it to ONNX for SwingLoop.

    pip install tensorflow tf2onnx
    python tools/export_movenet.py              # Lightning: fast, good for 30 Hz on any GPU
    python tools/export_movenet.py --thunder    # Thunder: more accurate, heavier

Writes windows/SwingLoop.Windows/Models/movenet.onnx. SwingLoop reads the
input size and type from the model, so either variant works. The phone apps
do not need this: they use Apple Vision and ML Kit pose detection.
"""

import argparse
import pathlib
import subprocess
import sys
import tarfile
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
CACHE = ROOT / "tools" / ".movenet"
OUT = ROOT / "windows" / "SwingLoop.Windows" / "Models" / "movenet.onnx"

SOURCES = {
    "lightning": [
        "https://www.kaggle.com/api/v1/models/google/movenet/tensorFlow2/singlepose-lightning/4/download",
        "https://tfhub.dev/google/movenet/singlepose/lightning/4?tf-hub-format=compressed",
    ],
    "thunder": [
        "https://www.kaggle.com/api/v1/models/google/movenet/tensorFlow2/singlepose-thunder/4/download",
        "https://tfhub.dev/google/movenet/singlepose/thunder/4?tf-hub-format=compressed",
    ],
}


def download(variant: str) -> pathlib.Path:
    target = CACHE / variant
    if (target / "saved_model.pb").exists():
        return target
    target.mkdir(parents=True, exist_ok=True)
    archive = CACHE / f"{variant}.tar.gz"
    last_error = None
    for url in SOURCES[variant]:
        try:
            print(f"Downloading {url}")
            urllib.request.urlretrieve(url, archive)
            with tarfile.open(archive) as tar:
                tar.extractall(target)
            if (target / "saved_model.pb").exists():
                return target
        except Exception as e:  # try the next mirror
            last_error = e
    sys.exit(f"Could not download MoveNet {variant}: {last_error}\n"
             "Download the SavedModel manually from Kaggle (google/movenet) and extract it to " + str(target))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--thunder", action="store_true", help="use the larger, more accurate model")
    args = parser.parse_args()
    variant = "thunder" if args.thunder else "lightning"

    saved_model = download(variant)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([sys.executable, "-m", "tf2onnx.convert", "--saved-model", str(saved_model),
                    "--output", str(OUT), "--opset", "13"], check=True)
    print(f"Wrote {OUT}")


if __name__ == "__main__":
    main()
