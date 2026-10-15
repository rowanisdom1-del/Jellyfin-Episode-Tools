#!/usr/bin/env bash
# convert_mkv_to_mp4.sh
#
# Remuxes MKV files to MP4 without re-encoding video or audio.
#
# Usage:
#   ./convert_mkv_to_mp4.sh [folder] [options]
#   ./convert_mkv_to_mp4.sh                       # dry run
#   ./convert_mkv_to_mp4.sh --apply               # convert, keep originals
#   ./convert_mkv_to_mp4.sh --apply --delete-originals
#   ./convert_mkv_to_mp4.sh --apply --force        # allow unusual codecs
#
# Needs ffmpeg and ffprobe: sudo apt install ffmpeg

set -uo pipefail

SERIES_DIR="/mnt/storage/media/"
APPLY=false
DELETE=false
FORCE=false
DIR_SET=false

usage() {
  cat <<'EOF'
Usage: convert_mkv_to_mp4.sh [folder] [options]

Options:
  --apply              Convert files. Without this, the script only previews.
  --delete-originals   Delete each MKV after its MP4 has been verified.
                       Requires --apply.
  --force              Also attempt codecs outside the common MP4 codec lists.
  -h, --help           Show this help.

Existing MP4s are never overwritten. Text subtitles are converted to mov_text;
unsupported subtitle streams are omitted.
EOF
}

for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=true ;;
    --delete-originals) DELETE=true ;;
    --force) FORCE=true ;;
    -h|--help) usage; exit 0 ;;
    --*)
      echo "Unknown option: $arg (see --help)" >&2
      exit 1
      ;;
    *)
      if $DIR_SET; then
        echo "Only one folder may be specified (see --help)." >&2
        exit 1
      fi
      SERIES_DIR=$arg
      DIR_SET=true
      ;;
  esac
done

SERIES_DIR="${SERIES_DIR%/}"
[[ -n "$SERIES_DIR" ]] || SERIES_DIR="/"

if $DELETE && ! $APPLY; then
  echo "--delete-originals requires --apply." >&2
  exit 1
fi

if ! command -v ffmpeg >/dev/null 2>&1 ||
   ! command -v ffprobe >/dev/null 2>&1; then
  echo "ffmpeg and ffprobe are required. Install them with: sudo apt install ffmpeg" >&2
  exit 1
fi

if [[ ! -d "$SERIES_DIR" ]]; then
  echo "Folder not found: $SERIES_DIR" >&2
  exit 1
fi

# Only one conversion run at a time.
if $APPLY && command -v flock >/dev/null 2>&1; then
  exec 9>/tmp/convert_mkv_to_mp4.lock
  if ! flock -n 9; then
    echo "Another conversion is already running." >&2
    exit 1
  fi
fi

# Run conversions at low priority.
PRIO=()
if command -v ionice >/dev/null 2>&1; then
  :
fi

CURRENT_TMP=""
cleanup() {
  if [[ -n "$CURRENT_TMP" ]]; then
    rm -f -- "$CURRENT_TMP"
  fi
}
trap cleanup EXIT
trap 'echo; echo "Interrupted; cleaning up the unfinished file."; exit 130' INT TERM HUP

# Populated by probe():
# VCOUNT, VCODECS[], VIDEO_MAPS[], ACOUNT, ACODECS[], AUDIO_MAPS[],
# SUBCOUNT, SUB_MAPS[], PIC_SUBS
#
# SUBCOUNT counts only text subtitle streams mapped into the MP4.
probe() {
  local input=$1 out line idx codec type
  VCOUNT=0
  ACOUNT=0
  SUBCOUNT=0
  PIC_SUBS=0
  VCODECS=()
  ACODECS=()
  VIDEO_MAPS=()
  AUDIO_MAPS=()
  SUB_MAPS=()

  out=$(ffprobe -v error \
    -show_entries stream=index,codec_name,codec_type \
    -of compact=p=0:nk=0 -i "$input" 2>/dev/null) || return 1
  [[ -n "$out" ]] || return 1

  while IFS= read -r line; do
    idx=""
    codec=""
    type=""

    [[ $line =~ index=([0-9]+) ]] && idx=${BASH_REMATCH[1]}
    [[ $line =~ codec_name=([^|]+) ]] && codec=${BASH_REMATCH[1]}
    [[ $line =~ codec_type=([a-z]+) ]] && type=${BASH_REMATCH[1]}

    [[ -n "$idx" && -n "$codec" ]] || continue

    case "$type" in
      video)
        # Ignore common cover-art formats.
        case "$codec" in
          mjpeg|png|bmp|gif) ;;
          *)
            VCOUNT=$((VCOUNT + 1))
            VCODECS+=("$codec")
            VIDEO_MAPS+=(-map "0:$idx")
            ;;
        esac
        ;;
      audio)
        ACOUNT=$((ACOUNT + 1))
        ACODECS+=("$codec")
        AUDIO_MAPS+=(-map "0:$idx")
        ;;
      subtitle)
        case "$codec" in
          subrip|srt|ass|ssa|webvtt|mov_text|text)
            SUBCOUNT=$((SUBCOUNT + 1))
            SUB_MAPS+=(-map "0:$idx")
            ;;
          *)
            PIC_SUBS=$((PIC_SUBS + 1))
            ;;
        esac
        ;;
    esac
  done <<< "$out"

  return 0
}

duration() {
  ffprobe -v error -show_entries format=duration \
    -of csv=p=0 -i "$1" 2>/dev/null | head -n 1
}

enough_space() {
  local size avail
  size=$(stat -c %s -- "$1") || return 1
  avail=$(df --output=avail -B1 -- "$(dirname -- "$1")" 2>/dev/null |
    tail -n 1 | tr -d ' ')
  [[ "$avail" =~ ^[0-9]+$ ]] || return 1
  (( avail > size + size / 20 + 104857600 ))
}

# Sets REASON and returns nonzero if the output does not pass verification.
# The local variables keep probe() from overwriting the source-file probe data.
verify() {
  local expected_v=$1 expected_a=$2 expected_s=$3 src=$4 new=$5
  local d1 d2 new_size
  local VCOUNT ACOUNT SUBCOUNT PIC_SUBS
  local -a VCODECS ACODECS VIDEO_MAPS AUDIO_MAPS SUB_MAPS

  if [[ ! -s "$new" ]]; then
    REASON="new file is empty"
    return 1
  fi

  probe "$new" || {
    REASON="ffprobe could not read the new file"
    return 1
  }

  if (( VCOUNT != expected_v )); then
    REASON="video track count changed ($expected_v to $VCOUNT)"
    return 1
  fi
  if (( ACOUNT != expected_a )); then
    REASON="audio track count changed ($expected_a to $ACOUNT)"
    return 1
  fi
  if (( SUBCOUNT != expected_s )); then
    REASON="mapped text subtitle track count changed ($expected_s to $SUBCOUNT)"
    return 1
  fi

  d1=$(duration "$src")
  d2=$(duration "$new")
  if ! awk -v a="$d1" -v b="$d2" '
    BEGIN {
      if (a !~ /^[0-9]+([.][0-9]+)?$/ ||
          b !~ /^[0-9]+([.][0-9]+)?$/) exit 1
      delta = a - b
      if (delta < 0) delta = -delta
      tolerance = a * 0.01
      if (tolerance < 2) tolerance = 2
      if (delta > tolerance) exit 1
    }'; then
    REASON="duration is missing or differs by more than 1% / 2 seconds"
    return 1
  fi

  new_size=$(stat -c %s -- "$new") || {
    REASON="could not read the new file size"
    return 1
  }
  if (( new_size < 1024 )); then
    REASON="new file is implausibly small (${new_size} bytes)"
    return 1
  fi

  return 0
}

is_safe_codec() {
  local kind=$1 codec=$2

  case "$kind/$codec" in
    video/h264|video/hevc|video/av1|video/vp9|video/mpeg4|\
    audio/aac|audio/ac3|audio/eac3|audio/mp3|audio/alac)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

converted=0
skipped=0
failed=0
total=0

while IFS= read -r -d '' src; do
  total=$((total + 1))
  out="${src%.*}.mp4"
  tmp="${out}.part"

  echo
  echo "File: $src"

  if [[ -e "$out" || -L "$out" ]]; then
    echo "  Skipping: destination already exists: $out"
    skipped=$((skipped + 1))
    continue
  fi
  if [[ -e "$tmp" || -L "$tmp" ]]; then
    echo "  Skipping: temporary file already exists: $tmp"
    skipped=$((skipped + 1))
    continue
  fi

  if ! probe "$src"; then
    echo "  Skipping: could not read streams with ffprobe."
    skipped=$((skipped + 1))
    continue
  fi

  if (( VCOUNT + ACOUNT == 0 )); then
    echo "  Skipping: no supported video or audio streams found."
    skipped=$((skipped + 1))
    continue
  fi

  unsafe=""
  for codec in "${VCODECS[@]}"; do
    if ! is_safe_codec video "$codec"; then
      unsafe+=" video:$codec"
    fi
  done
  for codec in "${ACODECS[@]}"; do
    if ! is_safe_codec audio "$codec"; then
      unsafe+=" audio:$codec"
    fi
  done

  if [[ -n "$unsafe" ]] && ! $FORCE; then
    echo "  Skipping: unusual MP4 codec(s):${unsafe}"
    echo "  Use --force to attempt this file."
    skipped=$((skipped + 1))
    continue
  fi

  if (( PIC_SUBS > 0 )); then
    echo "  Note: omitting $PIC_SUBS unsupported subtitle stream(s)."
  fi

  if ! $APPLY; then
    echo "  Would convert: $VCOUNT video, $ACOUNT audio, $SUBCOUNT text subtitle stream(s)"
    [[ -n "$unsafe" ]] && echo "  Warning: --force would be required for:${unsafe}"
    continue
  fi

  if ! enough_space "$src"; then
    echo "  Skipping: not enough free space for a safe conversion."
    skipped=$((skipped + 1))
    continue
  fi

  source_identity=$(stat -c '%d:%i:%s:%y' -- "$src") || {
    echo "  Skipping: could not identify source file."
    skipped=$((skipped + 1))
    continue
  }

  maps=("${VIDEO_MAPS[@]}" "${AUDIO_MAPS[@]}" "${SUB_MAPS[@]}")
  CURRENT_TMP=$tmp

  echo "  Converting..."
  if ! "${PRIO[@]}" ffmpeg -nostdin -hide_banner -loglevel warning \
      -i "$src" \
      "${maps[@]}" \
      -map_metadata 0 -map_chapters 0 \
      -c copy -c:s mov_text \
      -movflags +faststart -f mp4 "$tmp"; then
    echo "  Failed: ffmpeg could not create the MP4."
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  if ! verify "$VCOUNT" "$ACOUNT" "$SUBCOUNT" "$src" "$tmp"; then
    echo "  Failed verification: $REASON"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  # -n prevents overwriting a destination created by another process.
  if ! mv -nT -- "$tmp" "$out" || [[ -e "$tmp" ]]; then
    echo "  Failed: could not move verified output without overwriting."
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi
  CURRENT_TMP=""

  echo "  Created and verified: $out"
  converted=$((converted + 1))

  if $DELETE; then
    current_identity=$(stat -c '%d:%i:%s:%y' -- "$src" 2>/dev/null || true)
    if [[ "$current_identity" != "$source_identity" ]]; then
      echo "  Kept original: it changed during conversion."
    elif rm -- "$src"; then
      echo "  Deleted original: $src"
    else
      echo "  Warning: could not delete original: $src"
    fi
  fi
done < <(find "$SERIES_DIR" -type f -iname '*.mkv' -print0)

echo
echo "Finished: $total file(s) found; $converted converted, $skipped skipped, $failed failed."
if ! $APPLY; then
  echo "Dry run only. Use --apply to convert files."
fi

if (( failed > 0 )); then
  exit 1
fi
