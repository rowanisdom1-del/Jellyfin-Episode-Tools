#!/usr/bin/env python3
"""
audit.py

Read-only library audit:

  DUP   two or more video files that parse as the same episode
        (same show, same season and episode number) — the classic
        "episode exists twice, in different quality/encoding" problem
  BIG   the largest video files found, so you can decide what to delete
        when space runs out
  TOTAL disk usage per scanned folder

Usage:
    python3 audit.py --report /path/report.txt [folder ...]

Report lines are tab separated: DUP key path size | BIG size path | TOTAL size folder.
"""

import datetime
import os
import re
import sys

VIDEO_EXT = {".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"}

RE_SXE = re.compile(r"S(\d{1,2})[\s._-]*E(\d{1,3})", re.I)
RE_NXN = re.compile(r"(?<!\d)(\d{1,2})x(\d{2,3})(?!\d)", re.I)
TOP_BIG = 25


def episode_key(show, name):
    """Returns 'show|s|e' for files whose name contains a season/episode marker, else None."""
    m = RE_SXE.search(name) or RE_NXN.search(name)
    if not m:
        return None
    return f"{show.lower()}|{int(m.group(1)):02d}|{int(m.group(2)):02d}"


def human_size(num):
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(num) < 1024:
            return f"{num:.1f} {unit}" if unit != "B" else f"{int(num)} B"
        num /= 1024
    return f"{num:.1f} PB"


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

    if not folders:
        folders = ["/mnt/storage/media"]

    lines = []
    try:
        with open(report_file, "w", encoding="utf-8") as f:
            f.write(f"RUN\taudit\t{datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}\n")
    except OSError:
        report_file = ""

    print("=== LIBRARY AUDIT: read-only analysis, nothing is changed ===")
    print()

    groups = {}
    all_files = []
    folder_totals = []
    checked = 0

    for base in folders:
        base_size = 0
        if not os.path.isdir(base):
            print(f"Folder not found: {base}")
            continue
        for root, dirs, files in os.walk(base):
            dirs.sort()
            rel_root = os.path.relpath(root, base)
            show = rel_root.split(os.sep)[0] if rel_root != "." else "(root)"
            for fname in sorted(files):
                ext = os.path.splitext(fname)[1].lower()
                if ext not in VIDEO_EXT:
                    continue
                path = os.path.join(root, fname)
                try:
                    size = os.path.getsize(path)
                except OSError:
                    continue
                checked += 1
                base_size += size
                all_files.append((size, path))
                key = episode_key(show, fname)
                if key:
                    groups.setdefault(key, []).append((size, path))
        folder_totals.append((base_size, base))

    # Duplicates: same episode key with more than one file.
    dup_count = 0
    for key in sorted(groups):
        files = groups[key]
        if len(files) < 2:
            continue
        files.sort(reverse=True)  # biggest first
        dup_count += len(files)
        print(f"Duplicate episode: {key.split('|')[0]} S{key.split('|')[1]}E{key.split('|')[2]} ({len(files)} files)")
        for size, path in files:
            print(f"    {human_size(size)}  {path}")
            lines.append(f"DUP\t{key}\t{path}\t{size}")
        print()

    # Biggest files.
    all_files.sort(reverse=True)
    print(f"Largest {min(TOP_BIG, len(all_files))} video file(s):")
    for size, path in all_files[:TOP_BIG]:
        print(f"    {human_size(size)}  {path}")
        lines.append(f"BIG\t{size}\t{path}")
    print()

    # Folder totals.
    print("Folder totals:")
    for size, base in folder_totals:
        print(f"    {human_size(size)}  {base}")
        lines.append(f"TOTAL\t{size}\t{base}")

    print()
    print(f"Audit finished: {checked} video file(s) analysed, {dup_count} file(s) in duplicate groups.")

    if report_file and lines:
        try:
            with open(report_file, "a", encoding="utf-8") as f:
                f.write("\n".join(lines) + "\n")
        except OSError:
            pass


if __name__ == "__main__":
    main()
