#!/usr/bin/env python3
"""
subcheck.py

Analyses subtitle tracks of episodes and reports why a show might show song lyrics
instead of dialogue:

  - subtitle tracks titled signs / songs / lyrics / karaoke / titles / credits
  - the track flagged as DEFAULT being one of those (the classic anime problem:
    players show the lyrics track while dialogue is missing)
  - lyrics-heavy content (lines with the musical note ♪/♫)
  - many ultra-short cues (subtitles flashing for ~0.5s)
  - external .srt/.ass files with the same problems

Usage:
    python3 subcheck.py --report /path/report.txt [folder ...]

Changes nothing: this is a read-only check.
"""

import json
import re
import subprocess
import sys

VIDEO_EXT = {".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"}
SUB_EXT = {".srt", ".ass", ".ssa"}

RE_SUSPICIOUS_TITLE = re.compile(
    r"signs?|songs?|lyrics?|karaoke|full.?screen.?titles?|titles?|credits?", re.I)
RE_MUSICAL = re.compile(r"[♪♫]")
RE_SRT_TIMING = re.compile(
    r"(\d{2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{1,3})")
RE_ASS_TIMING = re.compile(r"^Dialogue:\s*\d+,(\d+):(\d+):(\d+)\.(\d+),(\d+):(\d+):(\d+)\.(\d+)", re.M)
RE_SRT_TEXT_LINE = re.compile(r"^\d+$")
MAX_EXTRACT_CUES = 4000  # safety valve for pathologically huge subtitle files


def ts_to_ms(h, m, s, ms):
    return ((int(h) * 60 + int(m)) * 60 + int(s)) * 1000 + int(ms)


def ffprobe_streams(path, ffprobe):
    """Returns the list of subtitle streams: dicts with index, language, title, is_default."""
    try:
        out = subprocess.run(
            [ffprobe, "-v", "error",
             "-show_entries",
             "stream=index,codec_name,codec_type:stream_tags=language,title:stream_disposition=default",
             "-select_streams", "s",
             "-of", "json", path],
            capture_output=True, text=True, timeout=60, check=True).stdout
        data = json.loads(out)
    except Exception:
        return []

    result = []
    for stream in data.get("streams", []):
        tags = stream.get("tags", {}) or {}
        result.append({
            "index": stream.get("index"),
            "codec": stream.get("codec_name", ""),
            "language": (tags.get("language") or "").lower(),
            "title": tags.get("title") or "",
            "default": bool((stream.get("disposition") or {}).get("default")),
        })
    return result


def extract_subtitle_text(path, stream_relative_index, ffmpeg):
    """Extracts one embedded subtitle track as SRT text (read-only, piped)."""
    try:
        out = subprocess.run(
            [ffmpeg, "-v", "error", "-i", path,
             "-map", f"0:s:{stream_relative_index}", "-f", "srt", "-"],
            capture_output=True, text=True, timeout=180, check=True).stdout
    except Exception:
        return None
    return out[:MAX_EXTRACT_CUES * 400]


def analyze_srt_text(text):
    """Returns (cues, musical_ratio, short_ratio) for subtitle text."""
    cues = []
    # SRT blocks: "1\n00:00:01,000 --> 00:00:03,000\ntext"
    blocks = re.split(r"\n\s*\n", text or "")
    for block in blocks:
        lines = [l for l in block.splitlines() if l.strip()]
        if not lines:
            continue
        timing = None
        text_lines = []
        for line in lines:
            m = RE_SRT_TIMING.search(line)
            if m:
                timing = m
            elif not RE_SRT_TEXT_LINE.match(line.strip()):
                text_lines.append(line.strip())
        if timing is None or not text_lines:
            continue
        g = timing.groups()
        start = ts_to_ms(g[0], g[1], g[2], g[3])
        end = ts_to_ms(g[4], g[5], g[6], g[7])
        cues.append({"start": start, "end": end, "text": " ".join(text_lines)})

    if not cues:
        return 0, 0.0, 0.0

    musical = sum(1 for c in cues if RE_MUSICAL.search(c["text"]))
    short = sum(1 for c in cues if (c["end"] - c["start"]) < 800 and c["end"] >= c["start"])
    return len(cues), musical / len(cues), short / len(cues)


def analyze_ass_text(text):
    """Parses [Events] Dialogue lines of .ass/.ssa; same metrics as SRT."""
    cues = []
    for m in RE_ASS_TIMING.finditer(text or ""):
        g = m.groups()
        start = ts_to_ms(g[0], g[1], g[2], g[3])
        end = ts_to_ms(g[4], g[5], g[6], g[7])
        # Dialogue line: layer,start,end,style,name,margin,margin,margin,effect,text
        rest = text[m.end():].split("\n", 1)[0]
        # skip the 8 fixed fields before the text
        parts = rest.split(",", 8)
        cue_text = parts[8] if len(parts) > 8 else ""
        cue_text = re.sub(r"\{[^}]*\}", "", cue_text)  # strip override tags
        if not cue_text.strip():
            continue
        cues.append({"start": start, "end": end, "text": cue_text})

    if not cues:
        return 0, 0.0, 0.0

    musical = sum(1 for c in cues if RE_MUSICAL.search(c["text"]))
    short = sum(1 for c in cues if (c["end"] - c["start"]) < 800 and c["end"] >= c["start"])
    return len(cues), musical / len(cues), short / len(cues)


def report(report_file, kind, path, message):
    if report_file:
        try:
            with open(report_file, "a", encoding="utf-8") as f:
                f.write(f"{kind}\t{path}\t{message}\n")
        except OSError:
            pass


def find_ffmpeg_tools():
    """ffmpeg/ffprobe: PATH first, then Jellyfin's bundled folder."""
    import shutil
    tools = {}
    for name in ("ffprobe", "ffmpeg"):
        found = shutil.which(name)
        tools[name] = found
    if tools["ffprobe"] and tools["ffmpeg"]:
        return tools
    for candidate in ("/usr/lib/jellyfin-ffmpeg", "/usr/local/bin"):
        ffprobe = f"{candidate}/ffprobe"
        ffmpeg = f"{candidate}/ffmpeg"
        import os
        if os.path.isfile(ffprobe) and os.access(ffprobe, os.X_OK):
            tools["ffprobe"] = tools["ffprobe"] or ffprobe
            tools["ffmpeg"] = tools["ffmpeg"] or ffmpeg
    return tools


def main():
    args = sys.argv[1:]
    report_file = ""
    i = 0
    folders = []
    while i < len(args):
        if args[i] == "--report" and i + 1 < len(args):
            report_file = args[i + 1]
            i += 2
        else:
            folders.append(args[i])
            i += 1

    tools = find_ffmpeg_tools()
    if not (tools.get("ffprobe") and tools.get("ffmpeg")):
        print("ffprobe and ffmpeg are required (Jellyfin's bundled ffmpeg is used automatically).")
        sys.exit(1)

    if report_file:
        try:
            with open(report_file, "w", encoding="utf-8") as f:
                f.write(f"RUN\tsubcheck\t{datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}\n")
        except OSError:
            report_file = ""

    print("=== SUBTITLE CHECK: read-only analysis, nothing is changed ===")
    print()

    if not folders:
        folders = ["/mnt/storage/media"]

    checked = 0
    problems = 0

    for base in folders:
        if not os.path.isdir(base):
            print(f"Folder not found: {base}")
            continue
        for root, dirs, files in os.walk(base):
            dirs.sort()
            for fname in sorted(files):
                ext = os.path.splitext(fname)[1].lower()
                if ext not in VIDEO_EXT:
                    continue
                path = os.path.join(root, fname)
                rel = os.path.relpath(path, base)
                print(f"File: {rel}")

                streams = ffprobe_streams(path, tools["ffprobe"])
                if not streams:
                    print("  no subtitle streams (or ffprobe failed)")
                    checked += 1
                    continue

                text_streams = [s for s in streams
                                if s["codec"] in ("subrip", "srt", "ass", "ssa", "webvtt",
                                                  "mov_text", "text", "dvb_teletext", "hdmv_pgs_subtitle")
                                or True]  # keep all; text-ness checked during extraction
                suspicious = [s for s in text_streams if RE_SUSPICIOUS_TITLE.search(s["title"] or "")]
                defaults = [s for s in text_streams if s["default"]]

                # Describe the tracks.
                for s in text_streams:
                    flag = "DEFAULT" if s["default"] else ""
                    desc = f"track #{s['index']} [{s['codec']}] lang={s['language'] or 'und'}"
                    if s["title"]:
                        desc += f" title='{s['title']}'"
                    if flag:
                        desc += f" << {flag}"
                    print(f"  {desc}")
                    report(report_file, "SUBINFO", path, desc)

                # The classic problem: the default track is signs/songs/lyrics.
                if len(text_streams) > 1:
                    for s in suspicious:
                        if s["default"]:
                            msg = (f"DEFAULT subtitle track looks like a signs/songs/lyrics track "
                                   f"(title='{s['title']}') while {len(text_streams) - 1} other "
                                   f"subtitle track(s) exist — this is why you see lyrics flashing. "
                                   f"Re-convert with 'Drop signs/songs subtitle tracks' enabled "
                                   f"(task 6) to fix.")
                            print(f"  !! {msg}")
                            report(report_file, "SUBWARN", path, msg)
                            problems += 1

                # Analyse the default track (or the only track).
                targets = defaults if defaults else text_streams[:1]
                for s in targets:
                    text = extract_subtitle_text(path, text_streams.index(s), tools["ffmpeg"])
                    if text is None:
                        # Image-based (PGS/VobSub) tracks cannot be analysed as text.
                        report(report_file, "SUBINFO", path,
                               f"track #{s['index']} could not be extracted as text (image-based subtitle)")
                        continue
                    cues, musical_ratio, short_ratio = analyze_srt_text(text)
                    if cues == 0:
                        report(report_file, "SUBINFO", path,
                               f"track #{s['index']} contains no analysable text (image-based subtitle?)")
                        continue
                    if musical_ratio > 0.4:
                        msg = (f"track #{s['index']} is lyrics-heavy: {int(musical_ratio * 100)}% of "
                               f"{cues} cues are song lines (♪) — you may be watching the songs track.")
                        print(f"  !! {msg}")
                        report(report_file, "SUBWARN", path, msg)
                        problems += 1
                    if short_ratio > 0.35:
                        msg = (f"track #{s['index']} has broken-feeling timing: {int(short_ratio * 100)}% of "
                               f"{cues} cues flash shorter than 0.8s.")
                        print(f"  !! {msg}")
                        report(report_file, "SUBWARN", path, msg)
                        problems += 1
                    if musical_ratio <= 0.4 and short_ratio <= 0.35:
                        msg = f"track #{s['index']} looks like normal dialogue ({cues} cues)."
                        report(report_file, "SUBOK", path, msg)

                # External subtitle files next to / in the same folder.
                for fname in files:
                    fext = os.path.splitext(fname)[1].lower()
                    if fext not in SUB_EXT:
                        continue
                    fpath = os.path.join(root, fname)
                    try:
                        with open(fpath, "r", encoding="utf-8", errors="replace") as f:
                            content = f.read(MAX_EXTRACT_CUES * 400)
                    except OSError:
                        continue
                    if fext == ".srt":
                        cues, musical_ratio, short_ratio = analyze_srt_text(content)
                    else:
                        cues, musical_ratio, short_ratio = analyze_ass_text(content)
                    if cues == 0:
                        continue
                    rel_sub = os.path.relpath(fpath, base)
                    if musical_ratio > 0.4:
                        msg = (f"external subtitle is lyrics-heavy: {int(musical_ratio * 100)}% of "
                               f"{cues} cues are song lines (♪): {rel_sub}")
                        print(f"  !! {msg}")
                        report(report_file, "SUBWARN", path, msg)
                        problems += 1
                    elif short_ratio > 0.35:
                        msg = (f"external subtitle has broken-feeling timing: {int(short_ratio * 100)}% "
                               f"of {cues} cues shorter than 0.8s: {rel_sub}")
                        print(f"  !! {msg}")
                        report(report_file, "SUBWARN", path, msg)
                        problems += 1
                    else:
                        report(report_file, "SUBOK", path,
                               f"external subtitle looks like dialogue ({cues} cues): {rel_sub}")

                checked += 1

    print()
    print(f"Sub check finished: {checked} episode(s) analysed, {problems} problem(s) found.")
    print("See Dashboard > Plugins > Episode Tools > Episode Tools Sub Check.")


if __name__ == "__main__":
    main()
