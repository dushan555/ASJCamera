"""Local stdio worker. Receives RGB24 frames from Unity; never opens the camera."""
import json
import struct
import sys
import time
from pathlib import Path

import mediapipe as mp
import numpy as np


def read_exact(stream, size):
    data = bytearray()
    while len(data) < size:
        chunk = stream.read(size - len(data))
        if not chunk:
            if not data:
                return None
            raise EOFError("Truncated frame")
        data.extend(chunk)
    return bytes(data)


def main():
    options = mp.tasks.vision.HandLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(Path(__file__).with_name("hand_landmarker.task"))),
        running_mode=mp.tasks.vision.RunningMode.VIDEO,
        num_hands=2,
        min_hand_detection_confidence=0.5,
        min_hand_presence_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    with mp.tasks.vision.HandLandmarker.create_from_options(options) as detector:
        print('{"ready":true}', flush=True)
        while True:
            header = read_exact(sys.stdin.buffer, 20)
            if header is None:
                break
            width, height, size, timestamp = struct.unpack("<IIIq", header)
            if not (0 < width <= 4096 and 0 < height <= 4096 and size == width * height * 3):
                raise ValueError("Invalid RGB frame header")
            raw = read_exact(sys.stdin.buffer, size)
            if raw is None:
                raise EOFError("Missing RGB frame")
            pixels = np.frombuffer(raw, dtype=np.uint8).reshape(height, width, 3)
            start = time.perf_counter()
            result = detector.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=pixels), timestamp)
            hands = []
            for landmarks, world, categories in zip(result.hand_landmarks, result.hand_world_landmarks, result.handedness):
                hands.append({
                    "label": categories[0].category_name,
                    "score": categories[0].score,
                    "points": [{"x": p.x, "y": p.y, "z": p.z} for p in landmarks],
                    "world": [{"x": p.x, "y": p.y, "z": p.z} for p in world],
                })
            print(json.dumps({"timestamp": timestamp, "milliseconds": (time.perf_counter()-start)*1000,
                              "hands": hands}, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    main()
