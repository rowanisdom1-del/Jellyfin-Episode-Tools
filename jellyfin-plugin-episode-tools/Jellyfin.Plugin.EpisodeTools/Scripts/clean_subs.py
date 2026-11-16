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
import shutil
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

# Flattened drawing paths from earlier (bad) conversions: "m 0 0 b 9 0 19.5 ..."
DRAW_CMD_RE = re.compile(r"(?<![A-Za-z0-9])[mlbncsMLBNCS](?:\s+-?\d+(?:\.\d+)?){2,}")
NUM_RUN_RE = re.compile(r"(?<![\w.])-?\d+(?:\.\d+)?(?:\s+-?\d+(?:\.\d+)?)+")


def strip_drawing_soup(text):
    """Remove coordinate soup glued to real text: "m 0 0 b 9 0 ...19.5Currently...".
    Iterates because removing one layer exposes the next (e.g. "289.4 0m 306.1")."""
    prev = None
    stripped = False
    while text and prev != text:
        prev = text
        new = DRAW_CMD_RE.sub(" ", text)
        new = NUM_RUN_RE.sub(" ", new)
        if new != text:
            stripped = True
        text = new
        # Orphaned command letters left once their numbers are gone ("0m 306.1"
        # -> NUM_RUN eats "0" -> bare "m"). Only after a real strip, so genuine
        # units like "5 m" and contractions ("Let's") stay untouched.
        if stripped:
            text = re.sub(r"(^|\s)([mlbncsMLBNCS])(?=\s|$)", r"\1 ", text)
    return re.sub(r"[ \t]+", " ", text).strip()


def _norm_seg(s):
    return re.sub(r"[\s.,!?;:'\"-]+", "", s).casefold()


# Genuine words that end in "m" - their trailing m must never be stripped.
KEEP_M = {
    "am", "from", "him", "them", "team", "teams", "dream", "dreams",
    "room", "rooms", "home", "homes", "name", "names", "time", "times",
    "problem", "problems", "system", "systems", "bottom", "bottoms",
    "autumn", "welcome", "welcomes", "confirm", "confirms", "perform",
    "performs", "inform", "informs", "transform", "transforms", "platform",
    "platforms", "album", "albums", "income", "assume", "assumes",
    "consume", "consumes", "resume", "seem", "seems", "boom", "zoom",
    "film", "films", "storm", "storms", "warm", "calm", "gram",
    "program", "programs", "diagram", "paragraph", "symptom", "symptoms",
    "kingdom", "random", "medium", "stadium", "museum", "vacuum",
    "algorithm", "algorithms", "mom",
}


def strip_glued_m(text):
    """Remove the drawing "m" (moveto) that earlier conversions glued onto
    segment text: "coming homem Ichikam Ninom" -> "coming home Ichika Nino".
    Tokens that are real m-ending words ("from", "team") are kept."""
    out = []
    for word in text.split(" "):
        if not word:
            continue
        if re.fullmatch(r"[mM]+", word):
            continue  # bare moveto leftovers ("library mmmmm...")
        stripped = word
        m = re.search(r"[mM]+$", stripped)
        if m and m.start() > 0 and stripped[m.start() - 1].isalpha() \
                and stripped.casefold() not in KEEP_M:
            stripped = stripped[:m.start()]
        if stripped:
            out.append(stripped)
    return " ".join(out)


def _norm_period(s, i):
    """At position i find identical copies glued together, ignoring spaces and
    case ("TheQuint...upletsThe Quintessential..."). Returns
    (first_copy_text, first_copy_end, consume_end) or None.

    Space-separated repeats ("No no no", "Yes yes") always have a space at the
    copy seam, so they fail the seam check and are left alone.
    """
    idx = []
    buf = []
    for p in range(i, len(s)):
        if s[p] != " ":
            idx.append(p)
            buf.append(s[p].casefold())
    norm = "".join(buf)
    m = len(norm)
    for L in range(4, m // 2 + 1):
        if norm[:L] != norm[L:2 * L]:
            continue
        if not re.search(r"[a-z]", norm[:L]):
            continue
        seam = idx[L]
        if seam - 1 < i or s[seam - 1] == " ":
            continue  # copies are space-separated: normal dialogue, keep
        k = 2
        while norm[L * k:L * (k + 1)] == norm[:L]:
            k += 1
        first_end = idx[L]
        consume_end = idx[L * k] if L * k < m else len(s)
        return s[i:first_end], first_end, consume_end
    return None


def collapse_repeats(text):
    """De-stack identical glued copies that earlier conversions flattened into one
    cue ("ItsukiItsukiItsukiItsukiRaiha...").

    Copies are glued boundary-to-boundary, so they are exactly periodic at the
    cursor; space-separated word repeats ("No no no no") never form a glued
    period at a word start and survive untouched.
    """
    def once(s):
        out = []
        i = 0
        n = len(s)
        trusted = True
        while i < n:
            # Only test for a period at a real boundary: the start, right after
            # a consumed copy (trusted), or a word start. Mid-word positions
            # produce false periods ("o no n" inside "No no no").
            hit = None
            if trusted or i == 0 or s[i - 1] == " ":
                hit = _norm_period(s, i)
            if hit is not None:
                unit, _first_end, consume_end = hit
                unit_len = len(unit)
                out.append(unit)
                i = consume_end
                # near-identical leftover copy ("School camp!" after "School Camp")
                while i < n:
                    skip = 0
                    for extra in (0, 1, 2, -1, -2):
                        L2 = unit_len + extra
                        if L2 > 0 and i + L2 <= n and _norm_seg(s[i:i + L2]) == _norm_seg(unit):
                            skip = L2
                            break
                    if skip:
                        i += skip
                    else:
                        break
                trusted = True
            else:
                out.append(s[i])
                i += 1
                trusted = False
        return "".join(out)

    # stacked doubled pairs ("NextNextStopStop") need a second pass to expose
    # the larger structure ("NextStopNextStop" -> "NextStop")
    for _ in range(8):
        new = once(text)
        if new == text:
            break
        text = new
    return text


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
    # Split into tags and literal text so drawing mode can be tracked: ASS signs
    # rendered as vector graphics ("{\p1}m 1 -5 l 4 -62 ...{\p0}") store path
    # coordinates as literal text - left in, they flash as strings of random
    # numbers and overflow mov_text's encoder (ERANGE).
    parts = re.split(r"(\{[^}]*\})", text)
    drawing = False
    kept = []
    for part in parts:
        if part.startswith("{") and part.endswith("}"):
            match = re.search(r"\\p(\d+)", part)
            if match:
                drawing = match.group(1) != "0"
            continue
        if not drawing:
            kept.append(part)
    text = "".join(kept)
    text = text.replace("\\N", " ").replace("\\n", " ").replace("\\h", " ")
    text = re.sub(r"[ \t]+", " ", text).strip()
    # Drawing paths flattened to literal text by an earlier conversion - some
    # glued to real dialogue ("...378.4Lantern Shop"), some pure soup.
    text = strip_drawing_soup(text)
    text = collapse_repeats(text)
    text = strip_glued_m(text)
    if text and re.fullmatch(r"[\s\d.,:mlbncpMLBNCP-]+", text) and re.search(r"\d", text):
        return ""
    return text


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
                # Identical overlay/karaoke copies of the same line: extend the
                # cue, never concatenate ("The Costume House" x10 on screen).
                if text == prev[2] or (
                        len(text) >= 4 and len(prev[2]) >= 4 and text in prev[2]):
                    prev[1] = max(prev[1], end)
                    continue
                if len(prev[2]) >= 4 and len(text) >= 4 and prev[2] in text:
                    # growing karaoke line ("The Costume" -> "The Costume House")
                    prev[2] = text
                    prev[1] = max(prev[1], end)
                    continue
                prev[1] = max(prev[1], end)
                prev[2] += text
                continue
        merged.append([start, end, text])

    # Zero/very short durations (letter runs that all share one timestamp) would be
    # invisible or strobe on screen; give them a readable minimum length.
    for cue in merged:
        cue[2] = collapse_repeats(cue[2])
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
    indices = [i for i in argv[3:] if i.isdigit()]
    if not indices:
        return 0
    os.makedirs(out_dir, exist_ok=True)
    tmpdir = tempfile.mkdtemp(prefix="epclean_", dir=out_dir)

    # One ffmpeg pass for every requested track. Fansub files scatter tens of
    # thousands of cues across a large container, so reading the file once
    # instead of once per track is the difference between seconds and minutes.
    # Outputs are named in0.ass, in1.ass, ... in the same order as `indices`.
    try:
        args = [FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
                "-i", source]
        for k, index in enumerate(indices):
            args += ["-map", f"0:{index}", "-f", "ass",
                     os.path.join(tmpdir, f"in{k}.ass")]
        subprocess.run(args, capture_output=True, timeout=600)
    except Exception as exc:  # never break the conversion over subtitles
        print(f"clean_subs: extraction: {exc}", file=sys.stderr)

    try:
        for k, index in enumerate(indices):
            ass_path = os.path.join(tmpdir, f"in{k}.ass")
            try:
                if not os.path.exists(ass_path) or os.path.getsize(ass_path) == 0:
                    continue
                cues = merge_micro_cues(load_cues(ass_path))
                if not cues:
                    continue
                out_path = os.path.join(out_dir, f"sub_{index}.srt")
                write_srt(cues, out_path)
                # Tab separated so the caller can pair outputs with stream indices
                # even when a stream failed to clean and is missing from the list.
                print(f"{index}\t{out_path}")
            except Exception as exc:  # never break the conversion over one stream
                print(f"clean_subs: stream {index}: {exc}", file=sys.stderr)
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
