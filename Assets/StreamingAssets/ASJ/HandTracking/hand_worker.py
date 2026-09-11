"""Shared-memory worker. Unity creates two named MMFs; we open and use them.

Protocol (stdio text lines):
  Unity  → Python : "RUN <width> <height> <timestamp_ms>"
  Python → Unity  : "ready"  (once, after model loads)
  Python → Unity  : "<hand_count> <inference_ms>"  (per frame)

Shared memory:
  ASJHandFrame   - Unity writes RGB24 pixels here before sending RUN
  ASJHandResult  - We write packed float32 results here before replying

Result layout (floats, little-endian):
  Per hand (65 floats): label(0=Left/1=Right)  score  x0 y0 z0 … x20 y20 z20
  Unused hand slots remain zero.
"""
import mmap
import struct
import sys
import time
from pathlib import Path

import mediapipe as mp
import numpy as np

FRAME_SHM_NAME  = "ASJHandFrame"
RESULT_SHM_NAME = "ASJHandResult"
MAX_HANDS        = 2
FLOATS_PER_HAND  = 65   # 1 label + 1 score + 21*3 coords
RESULT_BYTES     = MAX_HANDS * FLOATS_PER_HAND * 4


def main():
    options = mp.tasks.vision.HandLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(
            model_asset_path=str(Path(__file__).with_name("hand_landmarker.task"))
        ),
        running_mode=mp.tasks.vision.RunningMode.VIDEO,
        num_hands=MAX_HANDS,
        min_hand_detection_confidence=0.5,
        min_hand_presence_confidence=0.5,
        min_tracking_confidence=0.5,
    )

    frame_mm = result_mm = None

    with mp.tasks.vision.HandLandmarker.create_from_options(options) as detector:
        print("ready", flush=True)

        for line in sys.stdin:
            parts = line.strip().split()
            if len(parts) != 4 or parts[0] != "RUN":
                continue

            width     = int(parts[1])
            height    = int(parts[2])
            timestamp = int(parts[3])
            size      = width * height * 3

            # Open named MMFs created by Unity (open-existing semantics on Windows).
            if frame_mm is None:
                frame_mm  = mmap.mmap(-1, size,           tagname=FRAME_SHM_NAME)
                result_mm = mmap.mmap(-1, RESULT_BYTES,   tagname=RESULT_SHM_NAME)
            elif frame_mm.size() < size:
                frame_mm.close()
                frame_mm = mmap.mmap(-1, size, tagname=FRAME_SHM_NAME)

            # Zero-copy view of the frame buffer.
            frame_mm.seek(0)
            raw = frame_mm.read(size)
            pixels = np.frombuffer(raw, dtype=np.uint8).reshape(height, width, 3)

            t0     = time.perf_counter()
            result = detector.detect_for_video(
                mp.Image(image_format=mp.ImageFormat.SRGB, data=pixels), timestamp
            )
            ms = (time.perf_counter() - t0) * 1000

            # Pack results into result MMF as raw float32.
            out = np.zeros(MAX_HANDS * FLOATS_PER_HAND, dtype=np.float32)
            hand_count = 0
            for landmarks, categories in zip(result.hand_landmarks, result.handedness):
                if hand_count >= MAX_HANDS:
                    break
                base           = hand_count * FLOATS_PER_HAND
                out[base]      = 0.0 if categories[0].category_name == "Left" else 1.0
                out[base + 1]  = categories[0].score
                for j, p in enumerate(landmarks):
                    out[base + 2 + j * 3]     = p.x
                    out[base + 2 + j * 3 + 1] = p.y
                    out[base + 2 + j * 3 + 2] = p.z
                hand_count += 1

            result_mm.seek(0)
            result_mm.write(out.tobytes())

            print(f"{hand_count} {ms:.1f}", flush=True)

    if frame_mm:
        frame_mm.close()
    if result_mm:
        result_mm.close()


if __name__ == "__main__":
    main()
