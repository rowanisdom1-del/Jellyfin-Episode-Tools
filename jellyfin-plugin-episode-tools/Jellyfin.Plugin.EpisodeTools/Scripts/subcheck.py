#!/usr/bin/env python3
"""
subcheck.py

Analyses subtitle tracks of episodes and reports why a show might show song lyrics
instead of dialogue, or garbage subtitles (random numbers, ads, OCR junk):

  - subtitle tracks titled signs / songs / lyrics / karaoke / titles / credits
  - the track flagged as DEFAULT being one of those (the classic anime problem:
    players show the lyrics track while dialogue is missing)
  - lyrics-heavy content (lines with the musical note ♪/♫)
  - many ultra-short cues (subtitles flashing for ~0.5s)
  - garbage content: pure numbers, ad/spam lines, OCR junk (mostly non-letters,
    vowel-less gibberish)
  - external .srt/.ass files with the same problems

Usage:
    python3 subcheck.py --report /path/report.txt [folder ...]

Changes nothing: this is a read-only check.
"""

import datetime
import json
import os
import re
import shutil
import subprocess
import sys

VIDEO_EXT = {".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"}
SUB_EXT = {".srt", ".ass", ".ssa"}

RE_SUSPICIOUS_TITLE = re.compile(
    r"[^a-z](signs|songs|song|lyrics|karaoke|titles|credits)[^a-z]", re.I)
RE_MUSICAL = re.compile(r"[♪♫]")
RE_AD = re.compile(
    r"opensubtitles|subtitles? (by|from)|addic7ed|rarbg|sync (by|and corrected)|"
    r"release.?group|_subs_|subs please", re.I)
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


def _metrics(cues):
    """Returns (musical_ratio, short_ratio, garbage_ratio) for a cue list."""
    if not cues:
        return 0.0, 0.0, 0.0
    musical = sum(1 for c in cues if RE_MUSICAL.search(c["text"]))
    short = sum(1 for c in cues if (c["end"] - c["start"]) < 800 and c["end"] >= c["start"])
    garbage = 0
    for c in cues:
        body = c["text"].strip()
        garbage_cue = False
        if re.fullmatch(r"[\d\s.:,%-]+", body):
            garbage_cue = True                      # pure numbers/punctuation
        elif RE_AD.search(body):
            garbage_cue = True                      # ad/spam lines
        else:
            alpha = sum(ch.isalpha() for ch in body)
            if len(body) >= 8 and alpha / max(len(body), 1) < 0.3:
                garbage_cue = True                  # mostly non-letters: OCR junk
            else:
                words = re.findall(r"[A-Za-z]{4,}", body)
                if len(words) >= 3 and sum(1 for w in words if not re.search(r"[aeiouyAEIOUY]", w)) / len(words) > 0.6:
                    garbage_cue = True              # vowel-less OCR gibberish
        if garbage_cue:
            garbage += 1
    return musical / len(cues), short / len(cues), garbage / len(cues)


def analyze_srt_text(text):
    """Returns (cues, musical_ratio, short_ratio, garbage_ratio) for SRT text."""
    cues = []
    blocks = re.split(r"\n\s*\n", text or "")
    for block in blocks:
        lines = [l for l in block.splitlines() if l.strip()]
        if not lines:
            continue
        # The counter is the line before the timing line; everything after the timing
        # line is cue text (even when it is only digits — that is garbage we count).
        t_idx = next((i for i, l in enumerate(lines) if RE_SRT_TIMING.search(l)), None)
        if t_idx is None or t_idx + 1 >= len(lines):
            continue
        m = RE_SRT_TIMING.search(lines[t_idx])
        g = m.groups()
        start = ts_to_ms(g[0], g[1], g[2], g[3])
        end = ts_to_ms(g[4], g[5], g[6], g[7])
        body = " ".join(l.strip() for l in lines[t_idx + 1:] if l.strip())
        if not body:
            continue
        cues.append({"start": start, "end": end, "text": body})

    return (len(cues),) + _metrics(cues)


def analyze_ass_text(text):
    """Parses [Events] Dialogue lines of .ass/.ssa; same metrics as SRT."""
    cues = []
    for m in RE_ASS_TIMING.finditer(text or ""):
        g = m.groups()
        start = ts_to_ms(g[0], g[1], g[2], g[3])
        end = ts_to_ms(g[4], g[5], g[6], g[7])
        # Dialogue line: layer,start,end,style,name,marginL,marginR,marginV,effect,text.
        # The regex consumed layer/start/end; the text is everything after the next 6 fields.
        rest = text[m.end():].split("\n", 1)[0]
        rest = rest.lstrip(",")
        tokens = rest.split(",")
        cue_text = ",".join(tokens[6:]) if len(tokens) > 6 else ""
        cue_text = re.sub(r"\{[^}]*\}", "", cue_text)  # strip override tags
        if not cue_text.strip():
            continue
        cues.append({"start": start, "end": end, "text": cue_text})

    return (len(cues),) + _metrics(cues)


def report(report_file, kind, path, message):
    if report_file:
        try:
            with open(report_file, "a", encoding="utf-8") as f:
                f.write(f"{kind}\t{path}\t{message}\n")
        except OSError:
            pass


def find_ffmpeg_tools():
    """ffmpeg/ffprobe: PATH first, then Jellyfin's bundled folder."""
    tools = {}
    for name in ("ffprobe", "ffmpeg"):
        tools[name] = shutil.which(name)
    if tools["ffprobe"] and tools["ffmpeg"]:
        return tools
    for candidate in ("/usr/lib/jellyfin-ffmpeg", "/usr/local/bin"):
        ffprobe = f"{candidate}/ffprobe"
        ffmpeg = f"{candidate}/ffmpeg"
        if os.path.isfile(ffprobe) and os.access(ffprobe, os.X_OK):
            tools["ffprobe"] = tools["ffprobe"] or ffprobe
            tools["ffmpeg"] = tools["ffmpeg"] or ffmpeg
    return tools


CACHE_VERSION = 1
CACHE_MTIME_TOLERANCE = 2  # seconds; SMB/NFS write times can differ slightly


def load_cache(cache_file):
    """Loads the per-file sub check cache; {} on any problem."""
    if not cache_file:
        return {}
    try:
        with open(cache_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        if data.get("version") == CACHE_VERSION and isinstance(data.get("files"), dict):
            return data["files"]
    except Exception:
        pass
    return {}


def save_cache(cache_file, files):
    """Writes the cache atomically (tmp + rename); failures are ignored."""
    if not cache_file:
        return
    tmp = cache_file + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"version": CACHE_VERSION, "files": files}, f)
        os.replace(tmp, cache_file)
    except OSError:
        pass


def cache_entry_valid(entry, path):
    """True when a cached result still matches the file on disk: size + mtime, with the
    same 2s mtime tolerance the intro fingerprint cache uses for network mounts."""
    if not isinstance(entry, dict):
        return False
    try:
        st = os.stat(path)
    except OSError:
        return False
    return (entry.get("size") == st.st_size
            and abs(entry.get("mtime", -1e18) - st.st_mtime) <= CACHE_MTIME_TOLERANCE)


def check_episode(path, base, files, tools, report_file, emit, rep):
    """Analyses one video file (embedded tracks + external subs in the same folder).

    Prints nothing directly: stdout lines go through `emit` and report entries through
    `rep`, so callers can cache and replay unchanged files without re-running
    ffprobe/ffmpeg. Returns the number of problems found.
    """
    problems = 0

    streams = ffprobe_streams(path, tools["ffprobe"])
    if not streams:
        emit("  no subtitle streams (or ffprobe failed)")
        return 0

    text_streams = [s for s in streams]  # image-based tracks are noted during extraction
    suspicious = [s for s in text_streams if RE_SUSPICIOUS_TITLE.search(" " + (s["title"] or "") + " ")]

    # Describe the tracks.
    for s in text_streams:
        flag = "DEFAULT" if s["default"] else ""
        desc = f"track #{s['index']} [{s['codec']}] lang={s['language'] or 'und'}"
        if s["title"]:
            desc += f" title='{s['title']}'"
        if flag:
            desc += f" << {flag}"
        emit(f"  {desc}")
        rep("SUBINFO", desc)

    # The classic problem: the default track is signs/songs/lyrics.
    if len(text_streams) > 1:
        for s in suspicious:
            if s["default"]:
                msg = (f"DEFAULT subtitle track looks like a signs/songs/lyrics track "
                       f"(title='{s['title']}') while {len(text_streams) - 1} other "
                       f"subtitle track(s) exist — this is why you see lyrics flashing. "
                       f"Re-convert with 'Drop signs/songs subtitle tracks' enabled "
                       f"(task 6) to fix.")
                emit(f"  !! {msg}")
                rep("SUBWARN", msg)
                problems += 1
                # One-click fix metadata: recommend the first non-signs/songs track as
                # the new default. The relative indices (position among the subtitle
                # streams) are what ffmpeg (-disposition:s:N) and mkvpropedit (track:sN)
                # need; the absolute ffprobe indices identify tracks in reports.
                candidates = [t for t in text_streams
                              if t is not s and not RE_SUSPICIOUS_TITLE.search(" " + (t["title"] or "") + " ")]
                if candidates:
                    pick = candidates[0]
                    wrong_rel = text_streams.index(s)
                    pick_rel = text_streams.index(pick)
                    fix = (f"fixable: default track #{s['index']} is a signs/songs track; "
                           f"make track #{pick['index']} (lang={pick['language'] or 'und'}) the default "
                           f"| wrong={wrong_rel} pick={pick_rel}")
                    emit(f"  >> {fix.split(' | ')[0]}")
                    rep("SUBFIX", fix)

    # Analyse the default track (or the only track).
    targets = [(idx, s) for idx, s in enumerate(text_streams) if s["default"]]
    if not targets:
        targets = [(0, text_streams[0])] if text_streams else []
    for stream_pos, s in targets:
        text = extract_subtitle_text(path, stream_pos, tools["ffmpeg"])
        if text is None:
            # Image-based (PGS/VobSub) tracks cannot be analysed as text.
            rep("SUBINFO",
                f"track #{s['index']} could not be extracted as text (image-based subtitle)")
            continue
        cues, musical_ratio, short_ratio, garbage_ratio = analyze_srt_text(text)
        if cues == 0:
            rep("SUBINFO",
                f"track #{s['index']} contains no analysable text (image-based subtitle?)")
            continue
        if musical_ratio > 0.4:
            msg = (f"track #{s['index']} is lyrics-heavy: {int(musical_ratio * 100)}% of "
                   f"{cues} cues are song lines (♪) — you may be watching the songs track.")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        if short_ratio > 0.35:
            msg = (f"track #{s['index']} has broken-feeling timing: {int(short_ratio * 100)}% of "
                   f"{cues} cues flash shorter than 0.8s.")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        if musical_ratio <= 0.4 and short_ratio <= 0.35 and garbage_ratio > 0.3:
            msg = (f"track #{s['index']} looks like dialogue but {int(garbage_ratio * 100)}% of "
                   f"{cues} cues are garbage (numbers/ads/junk) — conversion can drop this "
                   f"track automatically ('Drop garbage subtitle tracks').")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        elif musical_ratio <= 0.4 and short_ratio <= 0.35:
            msg = f"track #{s['index']} looks like normal dialogue ({cues} cues)."
            rep("SUBOK", msg)

    # External subtitle files next to / in the same folder.
    for fname in files:
        fext = os.path.splitext(fname)[1].lower()
        if fext not in SUB_EXT:
            continue
        fpath = os.path.join(os.path.dirname(path), fname)
        try:
            with open(fpath, "r", encoding="utf-8", errors="replace") as f:
                content = f.read(MAX_EXTRACT_CUES * 400)
        except OSError:
            continue
        if fext == ".srt":
            cues, musical_ratio, short_ratio, garbage_ratio = analyze_srt_text(content)
        else:
            cues, musical_ratio, short_ratio, garbage_ratio = analyze_ass_text(content)
        if cues == 0:
            continue
        rel_sub = os.path.relpath(fpath, base)
        if garbage_ratio > 0.3:
            msg = (f"external subtitle is mostly garbage: {int(garbage_ratio * 100)}% of "
                   f"{cues} cues are numbers/ads/junk: {rel_sub}")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        elif musical_ratio > 0.4:
            msg = (f"external subtitle is lyrics-heavy: {int(musical_ratio * 100)}% of "
                   f"{cues} cues are song lines (♪): {rel_sub}")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        elif short_ratio > 0.35:
            msg = (f"external subtitle has broken-feeling timing: {int(short_ratio * 100)}% "
                   f"of {cues} cues shorter than 0.8s: {rel_sub}")
            emit(f"  !! {msg}")
            rep("SUBWARN", msg)
            problems += 1
        else:
            rep("SUBOK",
                f"external subtitle looks like dialogue ({cues} cues): {rel_sub}")

    return problems


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

    # Per-file result cache, stored next to the report (ScriptRunner always puts the
    # report in the plugin work folder). Unchanged files cost one stat per run instead
    # of a full ffprobe + ffmpeg extraction, which is what makes the recurring sweep
    # cheap on low-power servers. Without a report file (manual CLI runs) caching is off.
    cache_file = (os.path.join(os.path.dirname(os.path.abspath(report_file)), "subcheck-cache.json")
                  if report_file else "")
    cache = load_cache(cache_file)
    merged = {}
    for cached_path, entry in cache.items():
        if os.path.exists(cached_path):
            merged[cached_path] = entry

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

                entry = merged.get(path)
                if entry is not None and cache_entry_valid(entry, path):
                    # Replay the cached stdout block and re-emit the report entries: the
                    # output is identical to a fresh run, only much cheaper.
                    for line in entry.get("lines", []):
                        print(line)
                    for kind, msg in entry.get("reports", []):
                        report(report_file, kind, path, msg)
                    problems += entry.get("problems", 0)
                    checked += 1
                    continue

                print(f"File: {rel}")
                lines = [f"File: {rel}"]
                reports = []

                def emit(msg, _lines=lines):
                    _lines.append(msg)
                    print(msg)

                def rep(kind, msg, _reports=reports, _path=path):
                    report(report_file, kind, _path, msg)
                    _reports.append((kind, msg))

                p = check_episode(path, base, files, tools, report_file, emit, rep)
                problems += p
                checked += 1

                try:
                    st = os.stat(path)
                    merged[path] = {
                        "size": st.st_size,
                        "mtime": st.st_mtime,
                        "lines": lines,
                        "reports": reports,
                        "problems": p,
                    }
                except OSError:
                    pass

    save_cache(cache_file, merged)

    print()
    print(f"Sub check finished: {checked} episode(s) analysed, {problems} problem(s) found.")
    print("See Dashboard > Plugins > Episode Tools > Episode Tools Sub Check.")


if __name__ == "__main__":
    main()
