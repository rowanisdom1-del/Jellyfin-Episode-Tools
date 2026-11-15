#!/usr/bin/env python3
"""clean_subs.py <input video> <output dir> <stream index> [stream index ...]

Extracts each listed text subtitle stream to ASS with ffmpeg, then rewrites it as a
clean SRT that survives conversion to MP4 (mov_text):

  * strips ASS override tags (style/karaoke/positioning) - these otherwise leak into
    the converted file as literal text (e.g. "{=63}") and flash on screen;
  * merges micro-cues: typewriter/karaoke fansub tracks store one cue per letter,
    sometimes hundreds per second, which makes players flicker like crazy.

Prints one output path per line (only for streams that were cleaned successfully).
Exit code 0 even when some streams fail; the caller falls back to the original
stream for any missing output.
"""
import os
import re
import subprocess
import sys
import tempfile

FFMPEG = os.environ.get("JELLYFIN_FFMPEG") or "ffmpeg"
if not os.path.exists(FFMPEG):
    for cand in ("/usr/lib/jellyfin-ffmpeg/ffmpeg", "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg"):
        if os.path.exists(cand):
            FFMPEG = cand
            break

DIALOGUE_RE = re.compile(r"^Dialogue:\s*(.*)$")
OVERRIDE_RE = re.compile(r"\{[^}]*\}")
MICRO_CUE_S = 0.25      # a cue shorter than this may be a fragment of a bigger one
MERGE_GAP_S = 0.12      # fragments may be this far apart and still belong together
MIN_DURATION_S = 0.3    # merged/short cues get at least this long to stay readable


def parse_ass_time(value):
    """0:01:50.10 -> 110.1"""
    parts = value.strip().split(":")
    if len(parts) != 3:
        return None
    try:
        return int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2])
    except ValueError:
        return None


def fmt_srt_time(seconds):
    if seconds < 0:
        seconds = 0
    ms = int(round(seconds * 1000))
    h, rem = divmod(ms, 3600000)
    m, rem = divmod(rem, 60000)
    s, ms = divmod(rem, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def clean_text(text):
    # ASS override tags, and any literal tag-like leftovers from earlier conversions.
    text = OVERRIDE_RE.sub("", text)
    text = text.replace("\\N", " ").replace("\\n", " ").replace("\\h", " ")
    return re.sub(r"[ \t]+", " ", text).strip()


def load_cues(ass_path):
    cues = []
    with open(ass_path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = DIALOGUE_RE.match(line.strip())
            if not match:
                continue
            fields = match.group(1).split(",", 9)
            if len(fields) < 10:
                continue
            start = parse_ass_time(fields[1])
            end = parse_ass_time(fields[2])
            if start is None or end is None:
                continue
            text = clean_text(fields[9])
            if not text:
                continue
            cues.append([start, max(end, start), text])
    return cues


def merge_micro_cues(cues):
    merged = []
    for start, end, text in cues:
        if merged:
            prev = merged[-1]
            same_start = abs(start - prev[0]) < 0.005
            prev_is_fragment = (prev[1] - prev[0]) < MICRO_CUE_S
            if same_start or (prev_is_fragment and start <= prev[1] + MERGE_GAP_S):
                prev[1] = max(prev[1], end)
                prev[2] += text
                continue
        merged.append([start, end, text])

    # Zero/very short durations (letter runs that all share one timestamp) would be
    # invisible or strobe on screen; give them a readable minimum length.
    for cue in merged:
        if cue[1] - cue[0] < MIN_DURATION_S:
            cue[1] = cue[0] + MIN_DURATION_S
    return merged


def write_srt(cues, out_path):
    with open(out_path, "w", encoding="utf-8") as handle:
        for i, (start, end, text) in enumerate(cues, 1):
            handle.write(f"{i}\n{fmt_srt_time(start)} --> {fmt_srt_time(end)}\n{text}\n\n")


def main(argv):
    if len(argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2

    source = argv[1]
    out_dir = argv[2]
    os.makedirs(out_dir, exist_ok=True)

    for index in argv[3:]:
        if not index.isdigit():
            continue
        try:
            with tempfile.NamedTemporaryFile(
                    suffix=".ass", dir=out_dir, delete=False, mode="w", encoding="utf-8") as tmp:
                ass_path = tmp.name

            result = subprocess.run(
                [FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
                 "-i", source, "-map", f"0:{index}", "-f", "ass", ass_path],
                capture_output=True, timeout=90)
            if result.returncode != 0 or not os.path.exists(ass_path):
                continue

            cues = merge_micro_cues(load_cues(ass_path))
            os.unlink(ass_path)
            if not cues:
                continue

            out_path = os.path.join(out_dir, f"sub_{index}.srt")
            write_srt(cues, out_path)
            # Tab separated so the caller can pair outputs with stream indices even
            # when a stream failed to clean and is missing from the list.
            print(f"{index}\t{out_path}")
        except Exception as exc:  # never break the conversion over one stream
            print(f"clean_subs: stream {index}: {exc}", file=sys.stderr)
            try:
                os.unlink(ass_path)
            except Exception:
                pass

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
