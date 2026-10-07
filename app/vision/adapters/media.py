"""ffmpeg/ffprobe adapter. Imported media is untrusted: bounded duration, size and frame count."""
from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess

from vision.domain import ValidationError

MAX_SECONDS = 600
MAX_SIDE = 4096


class FfmpegTools:
    @staticmethod
    def _require():
        if not (shutil.which("ffmpeg") and shutil.which("ffprobe")):
            raise ValidationError("ffmpeg and ffprobe are not installed on this host, so videos cannot be imported or extracted here.")

    def probe(self, video: Path):
        self._require()
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
             "stream=width,height,avg_frame_rate:format=duration", "-of", "json", str(video)],
            capture_output=True, text=True, timeout=60, check=False)
        try:
            data = json.loads(out.stdout)
            stream, seconds = data["streams"][0], float(data["format"]["duration"])
            num, den = stream["avg_frame_rate"].split("/")
            info = {"width": int(stream["width"]), "height": int(stream["height"]),
                    "duration": seconds, "fps": float(num) / float(den)}
        except (KeyError, IndexError, ValueError, ZeroDivisionError):
            raise ValidationError("The file is not a readable video.") from None
        if not 0 < info["duration"] <= MAX_SECONDS or not 0 < max(info["width"], info["height"]) <= MAX_SIDE:
            raise ValidationError(f"Video must be under {MAX_SECONDS}s and {MAX_SIDE}px per side.")
        return info

    def extract_frames(self, video: Path, out: Path, fps: float, limit: int):
        self._require()
        out.mkdir(parents=True, exist_ok=True)
        subprocess.run(
            ["ffmpeg", "-nostdin", "-v", "error", "-i", str(video), "-vf", f"fps={fps}",
             "-frames:v", str(limit), "-q:v", "2", str(out / "%06d.jpg")],
            check=True, timeout=1800)
        return sorted(out.glob("*.jpg"))
