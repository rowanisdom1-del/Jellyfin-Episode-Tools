#!/usr/bin/env python3
"""
rename_episodes.py

Renames episode files to the Jellyfin-friendly format and KEEPS the episode title:

    Show Name - S01E02 - Episode Title.mkv

Subtitles keep their language tag:  Show Name - S01E02 - Episode Title.en.srt

Usage:
    python3 rename_episodes.py                  # DRY RUN: only shows what would change
    python3 rename_episodes.py --apply          # really rename the files
    python3 rename_episodes.py /other/folder    # use another folder than the default

Expected layout:  /mnt/storage/media/Series/<Show Name>/<anything>/<episode files>
Safe to run more than once: files that are already correct are skipped and
nothing is ever overwritten.
"""

import os
import re
import sys

SERIES_DIR = "/mnt/storage/media/Series"

# Some shows number episodes across the whole series (013, 014, ...) instead of per season.
# Here you say how many episodes to subtract per season. Only used for names like "Show - 014 - Title".
ABSOLUTE_OFFSETS = {
    "Solo Leveling": {2: 12},   # season 2 starts at absolute episode 13
}

VIDEO_EXT = {"mkv", "mp4", "avi", "m4v", "mov", "wmv", "ts"}
SUB_EXT = {"srt", "ass", "ssa", "sub", "idx", "vtt"}
LANGS = {"en", "eng", "nl", "nld", "dut", "fr", "fre", "de", "ger", "es", "spa", "ita",
         "pt", "ja", "jpn", "ko", "kor", "zh", "ru", "ar", "sv", "pl", "tr"}
SMALL_WORDS = {"a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet", "at", "by",
               "in", "of", "on", "to", "up", "as", "with", "from"}

RE_SXE = re.compile(r"S(\d{1,2})[\s._-]*E(\d{1,3})", re.I)
RE_NXN = re.compile(r"(?<!\d)(\d{1,2})x(\d{2,3})(?!\d)", re.I)
RE_ABS = re.compile(r"\s-\s(\d{3})\s-\s")
RE_SEASON_WORD = re.compile(r"season[\s._-]*(\d{1,2})", re.I)
RE_SEASON_S = re.compile(r"(?<![A-Za-z])S(\d{1,2})(?!\d)", re.I)
RE_EP_WORD = re.compile(r"(?<![A-Za-z])(?:episode|ep|e)[\s._-]*(\d{1,3})(?!\d)", re.I)
RE_LEAD = re.compile(r"^(\d{1,3})(?!\d)")
# Where the title stops: quality / release tags or brackets
RE_CUT = re.compile(
    r"[\s._-]*(?:\(|\[|\d{3,4}p\b|BluRay|WEB[-.\s]?DL|WEBRip|HDTV|DLRip|x26[45]|HEVC|(?-i:ENGLiSH))",
    re.I,
)


def smart_case(title):
    """Turn 'DON'T LEAVE ME HANGING HERE' into 'Don't Leave Me Hanging Here'."""
    out = []
    for i, word in enumerate(title.split(" ")):
        core = word.strip(",.!?;:").lower()
        if i > 0 and core in SMALL_WORDS:
            out.append(word.lower())
        else:
            out.append("-".join(p.capitalize() for p in word.split("-")))
    return " ".join(out)


def clean_title(rest):
    rest = re.sub(r"^[\s._-]+", "", rest.strip())
    m = RE_CUT.search(rest)
    if m:
        rest = rest[:m.start()]
    rest = rest.strip(" ._-")
    if not rest:
        return ""
    if " " not in rest:  # dotted style: The.Strongest.Man
        rest = rest.replace(".", " ").replace("_", " ")
        rest = re.sub(r"(\w) s\b", r"\1's", rest)  # Mom s -> Mom's
    rest = rest.replace(":", " -")
    rest = re.sub(r'[<>"/\\|?*]', "", rest)
    rest = re.sub(r"\s+", " ", rest).strip(" ._-")
    if rest.isupper():
        rest = smart_case(rest)
    rest = re.sub(r"(\d)(St|Nd|Rd|Th)\b", lambda m: m.group(1) + m.group(2).lower(), rest)
    return rest


def folder_season(parent_dirs):
    """Season number from the nearest folder name that has one, e.g. 'Season 02' or 'S04'."""
    for folder in reversed(parent_dirs):
        m = RE_SEASON_WORD.search(folder) or RE_SEASON_S.search(folder)
        if m:
            return int(m.group(1))
    return None


def parse(show, parent_dirs, name):
    """Returns (season, episode, title) or None."""
    m = RE_SXE.search(name)
    if m:
        return int(m.group(1)), int(m.group(2)), clean_title(name[m.end():])
    m = RE_NXN.search(name)
    if m:
        return int(m.group(1)), int(m.group(2)), clean_title(name[m.end():])
    season = folder_season(parent_dirs)
    if season is None:
        return None
    m = RE_ABS.search(name)
    if m:
        ep = int(m.group(1)) - ABSOLUTE_OFFSETS.get(show, {}).get(season, 0)
        return (season, ep, clean_title(name[m.end():])) if ep >= 1 else None
    m = RE_EP_WORD.search(name) or RE_LEAD.search(name)
    if m:
        return season, int(m.group(1)), clean_title(name[m.end():])
    return None


def new_name(show, parent_dirs, fname):
    """The wanted file name, or None if season/episode could not be found. Non-media files give ''."""
    stem, dot, ext = fname.rpartition(".")
    if not dot:
        return ""
    ext_l = ext.lower()
    if ext_l not in VIDEO_EXT and ext_l not in SUB_EXT:
        return ""
    lang = ""
    if ext_l in SUB_EXT:
        m = re.search(r"\.([A-Za-z]{2,3})$", stem)
        if m and m.group(1).lower() in LANGS:
            lang = "." + m.group(1).lower()
            stem = stem[:m.start()]
    parsed = parse(show, parent_dirs, stem)
    if not parsed:
        return None
    season, ep, title = parsed
    base = f"{show} - S{season:02d}E{ep:02d}"
    if title:
        base += f" - {title}"
    return f"{base}{lang}.{ext}"


def main():
    args = sys.argv[1:]
    apply = "--apply" in args
    paths = [a for a in args if not a.startswith("--")]
    series = paths[0] if paths else SERIES_DIR

    if not os.path.isdir(series):
        print(f"Folder {series} not found.")
        parent = os.path.dirname(series.rstrip("/"))
        if os.path.isdir(parent):
            print(f"Folders inside {parent} (capitals matter):")
            for item in sorted(os.listdir(parent)):
                print("  " + item)
        else:
            print("Is the drive mounted?")
        sys.exit(1)

    print("=== APPLY MODE: files will be renamed ===" if apply
          else "=== DRY RUN: nothing will be changed (use --apply to rename) ===")
    print()

    renamed = skipped = unmatched = 0
    claimed = set()

    for root, dirs, files in os.walk(series):
        dirs.sort()
        rel_root = os.path.relpath(root, series)
        if rel_root == ".":
            continue
        parts = rel_root.split(os.sep)
        show, parent_dirs = parts[0], parts[1:]

        for fname in sorted(files):
            wanted = new_name(show, parent_dirs, fname)
            rel = os.path.join(rel_root, fname)
            if wanted == "":
                continue
            if wanted is None:
                print(f"?? Could not detect season/episode: {rel}")
                unmatched += 1
                continue
            if wanted == fname:
                skipped += 1
                continue
            src = os.path.join(root, fname)
            dst = os.path.join(root, wanted)
            if os.path.exists(dst) or dst in claimed:
                print(f"!! Target already exists, skipping: {rel} -> {wanted}")
                skipped += 1
                continue
            claimed.add(dst)
            print(rel)
            print(f"   -> {wanted}")
            if apply:
                os.rename(src, dst)
            renamed += 1

    print()
    print(f"Done. To rename: {renamed} | already correct/skipped: {skipped} | not recognised: {unmatched}")
    if not apply:
        print("This was a dry run. Run again with --apply to do it for real.")


if __name__ == "__main__":
    main()
