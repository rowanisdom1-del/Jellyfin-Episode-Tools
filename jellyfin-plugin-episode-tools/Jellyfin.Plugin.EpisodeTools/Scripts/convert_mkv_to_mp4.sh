#!/usr/bin/env bash
# convert_mkv_to_mp4.sh
#
# Remuxes MKV files to MP4 without re-encoding video or audio.
#
# Usage:
#   ./convert_mkv_to_mp4.sh [folder ...] [options]
#   ./convert_mkv_to_mp4.sh                       # dry run
#   ./convert_mkv_to_mp4.sh --apply               # convert, keep originals
#   ./convert_mkv_to_mp4.sh --apply --delete-originals
#   ./convert_mkv_to_mp4.sh --apply --force        # allow unusual codecs
#   ./convert_mkv_to_mp4.sh --apply --audio-to-aac # DTS/TrueHD/FLAC audio -> AAC, video copied
#   ./convert_mkv_to_mp4.sh --report /path/file.txt /media/a /media/b
#
# Any number of folders may be given; each is searched (including sub folders)
# for .mkv files.

set -uo pipefail

SERIES_DIR="/mnt/storage/media/"
APPLY=false
DELETE=false
FORCE=false
AUDIO_TO_AAC=false
FASTSTART=true
DROP_SIGNS_SONGS=false
DROP_GARBAGE_SUBS=false
REDO=false
REMUX=false
REPAIR=false
AUTO_REPAIR=false
MIN_FREE_GB=10
REPORT_FILE=""
PREFS_FILE=""
PAUSE_FLAG=""
FOLDERS=()

usage() {
  cat <<'EOF'
Usage: convert_mkv_to_mp4.sh [folder ...] [options]

Folders:
  Any number of folders. Each one is searched (including sub folders) for .mkv
  files. With no folder, /mnt/storage/media/ is used.

Options:
  --apply              Convert files. Without this, the script only previews.
  --delete-originals   Delete each MKV after its MP4 has been verified.
                       Requires --apply.
  --force              Also attempt codecs outside the common MP4 codec lists.
  --audio-to-aac       Instead of skipping files whose audio MP4 does not handle well
                       (DTS, TrueHD, FLAC...), re-encode only those audio tracks to AAC.
                       The video is still copied untouched. AAC tracks are copied as they are.
  --report FILE        Write a report of every file that cannot be converted and why,
                       as tab separated lines: CANNOT <tab> path <tab> reason.
  --no-faststart       Skip the faststart optimization (moving the MP4 index to the
                       front of the file). Faster conversion, half the writes.
                       Jellyfin streams with range requests and works fine without it.
  --prefs FILE         Per-show track preferences, one line per show:
                       show <tab> subtitle-lang <tab> audio-lang.
                       Empty language = keep all; "none" subtitles = drop them.
  --pause-flag FILE    (internal) Path of the playback guard flag file; the script
                       waits between files while it contains "paused".
  --drop-signs-songs   Drop subtitle tracks whose title says signs/songs/lyrics/karaoke
                       (typical anime tracks) as long as another dialogue track exists.
  --drop-garbage-subS  Drop subtitle tracks whose content is mostly garbage (numbers,
                       ads, spam lines) when a better track exists. Requires python3;
                       skipped silently when python3 is missing.
  --redo               Also convert files whose .mp4 already exists and REPLACE the
                       .mp4 after verification. Use after changing preferences/settings.
  --remux-existing     Also re-mux existing .mp4 files that have no .mkv source,
                       replacing them after verification. Re-applies track preferences
                       and cleans subtitle text. Used by the per-show Convert button.
  --repair-mp4          Scan existing .mp4 files and losslessly repair the ones that
                        would fail Direct Play on mobile (MP4 index at the end of the
                        file, HEVC tagged hev1 instead of hvc1, bin_data streams).
                        Files that are already clean are skipped instantly.
  --auto-repair-mp4    Auto mobile repair for new conversions: faststart is forced for
                       every newly created .mp4, and after creation each new file is
                       probed and losslessly remuxed in place if it still would fail
                       Direct Play on mobile. Hi10P is reported, never re-encoded.
  --min-free-gb N      Require at least N GB free on the destination before converting
                       a file (default 10). Files below the threshold are skipped.
  -h, --help           Show this help.

Existing MP4s are never overwritten. Text subtitles are converted to mov_text;
unsupported subtitle streams are omitted.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply) APPLY=true ;;
    --delete-originals) DELETE=true ;;
    --force) FORCE=true ;;
    --audio-to-aac) AUDIO_TO_AAC=true ;;
    --no-faststart) FASTSTART=false ;;
    --drop-signs-songs) DROP_SIGNS_SONGS=true ;;
    --drop-garbage-subs) DROP_GARBAGE_SUBS=true ;;
    --redo) REDO=true ;;
    --remux-existing) REMUX=true ;;
    --repair-mp4) REPAIR=true ;;
    --auto-repair-mp4) AUTO_REPAIR=true ;;
    --min-free-gb)
      if [[ $# -lt 2 || ! "$2" =~ ^[0-9]+$ ]]; then
        echo "--min-free-gb needs a number (see --help)." >&2
        exit 1
      fi
      MIN_FREE_GB=$2
      shift
      ;;
    --prefs)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--prefs needs a file path (see --help)." >&2
        exit 1
      fi
      PREFS_FILE=$2
      shift
      ;;
    --pause-flag)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--pause-flag needs a file path (see --help)." >&2
        exit 1
      fi
      PAUSE_FLAG=$2
      shift
      ;;
    --report)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--report needs a file path (see --help)." >&2
        exit 1
      fi
      REPORT_FILE=$2
      shift
      ;;
    -h|--help) usage; exit 0 ;;
    --*)
      echo "Unknown option: $1 (see --help)" >&2
      exit 1
      ;;
    *)
      FOLDERS+=("$1")
      ;;
  esac
  shift
done

if (( ${#FOLDERS[@]} == 0 )); then
  FOLDERS=("$SERIES_DIR")
fi

if $DELETE && ! $APPLY; then
  echo "--delete-originals requires --apply." >&2
  exit 1
fi

for folder in "${FOLDERS[@]}"; do
  if [[ ! -d "$folder" ]]; then
    echo "Folder not found: $folder (the other folders are still scanned)" >&2
  fi
done

# The report page in the dashboard reads this file. Header line first, then one
# CANNOT line per file that cannot be converted.
if [[ -n "$REPORT_FILE" ]]; then
  mode=preview
  $APPLY && mode=apply
  printf 'RUN\t%s\t%s\n' "$mode" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$REPORT_FILE" 2>/dev/null || REPORT_FILE=""
fi

# report <path> <reason>: remember a file that cannot be converted.
report() {
  [[ -n "$REPORT_FILE" ]] || return 0
  printf 'CANNOT\t%s\t%s\n' "$1" "$2" >> "$REPORT_FILE" 2>/dev/null || true
}

# wait_for_playback: when the plugin passed --pause-flag FILE, that file says "paused"
# while someone is watching and "run" otherwise (a C# watchdog keeps it current). Waiting
# happens here, at the top of the per-file loop, so nothing heavy runs during playback;
# INT/TERM still interrupt the sleep immediately, so a Stop never waits.
wait_for_playback() {
  [[ -n "$PAUSE_FLAG" ]] || return 0
  local said=""
  while [[ "$(cat -- "$PAUSE_FLAG" 2>/dev/null)" == "paused" ]]; do
    if [[ -z "$said" ]]; then
      echo "  Paused: someone is watching; continuing when playback stops."
      said=1
    fi
    sleep 15
  done
}

# ---- per-show subtitle/audio preferences (Episode Tools Shows page) ----
PREF_SHOW=(); PREF_SUB=(); PREF_AUD=(); PREF_AUDFB=()
if [[ -n "$PREFS_FILE" && -f "$PREFS_FILE" ]]; then
  while IFS=$'\t' read -r s sub aud audfb; do
    [[ -n "$s" ]] || continue
    PREF_SHOW+=("$s"); PREF_SUB+=("${sub:-}"); PREF_AUD+=("${aud:-}"); PREF_AUDFB+=("${audfb:-}")
  done < "$PREFS_FILE"
fi

# lang_matches <stream-lang> <pref-lang>: true when the stream matches the preference.
lang_matches() {
  local s="${1,,}" p="${2,,}"
  [[ -n "$p" && "$p" != "none" ]] || return 1
  [[ "$s" == "$p" ]] && return 0
  case "$p:$s" in
    nl:nld|en:eng|de:ger|de:deu|fr:fre|fr:fra|es:spa|it:ita|ja:jpn|ko:kor|\
zh:zho|zh:chi|pt:por|ru:rus|sv:swe|pl:pol|tr:tur|ar:ara) return 0 ;;
  esac
  [[ ${#p} -eq 2 && "$s" == "$p"* ]] && return 0
  return 1
}

# get_show_pref <show-name>: sets SUBPREF, AUDPREF and AUDPREF2 (empty = keep all).
get_show_pref() {
  SUBPREF=""
  AUDPREF=""
  AUDPREF2=""
  local want="${1,,}" i
  for i in "${!PREF_SHOW[@]}"; do
    if [[ "${PREF_SHOW[$i],,}" == "$want" ]]; then
      SUBPREF="${PREF_SUB[$i]:-}"
      AUDPREF="${PREF_AUD[$i]:-}"
      AUDPREF2="${PREF_AUDFB[$i]:-}"
      return 0
    fi
  done
}

# show_name_for <file-path>: the show is the first folder under the scanned folder.
# When the scanned folder IS the show folder (per-show conversion), the folder name is the show.
show_name_for() {
  local p=$1 base rel
  for base in "${FOLDERS[@]}"; do
    base="${base%/}/"
    if [[ "$p/" == "$base"* ]]; then
      rel="${p#"$base"}"
      if [[ "$rel" == */* ]]; then
        printf '%s' "${rel%%/*}"
      else
        # File directly inside the scanned folder: the folder itself is the show.
        printf '%s' "$(basename "${base%/}")"
      fi
      return 0
    fi
  done
  printf '%s' "$(basename "$(dirname "$p")")"
}

if ! command -v ffmpeg >/dev/null 2>&1 ||
   ! command -v ffprobe >/dev/null 2>&1; then
  echo "ffmpeg and ffprobe are required. Install them with: sudo apt install ffmpeg" >&2
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

# Without flock there is no cross-run lock; tagging the temp file with this run's PID
# keeps two concurrent runs from writing the same .part file (the leftover-“.part” skip
# check then also stays per-run).
PART_SUFFIX=".part"
if $APPLY && ! command -v flock >/dev/null 2>&1; then
  PART_SUFFIX=".part.$$"
fi

# Run conversions at low priority, so Jellyfin keeps responding when someone is
# watching while a conversion is running.
PRIO=()
if command -v nice >/dev/null 2>&1; then
  PRIO+=(nice -n 19)
fi
if command -v ionice >/dev/null 2>&1; then
  PRIO+=(ionice -c 3)
fi

CURRENT_TMP=""
CLEAN_DIR=""
GARBAGE_DIR=""
cleanup() {
  if [[ -n "$CURRENT_TMP" ]]; then
    rm -f -- "$CURRENT_TMP"
  fi
  if [[ -n "${CLEAN_DIR:-}" ]]; then
    rm -rf -- "$CLEAN_DIR"
  fi
  if [[ -n "${GARBAGE_DIR:-}" ]]; then
    rm -rf -- "$GARBAGE_DIR"
  fi
}
trap cleanup EXIT
trap 'echo; echo "Interrupted; cleaning up the unfinished file."; exit 130' INT TERM HUP

# Populated by probe():
# VCOUNT, VCODECS[], VIDEO_MAPS[], ACOUNT, ACODECS[], AUDIO_MAPS[], ALANGS[],
# SUBCOUNT, SUB_MAPS[], PIC_SUBS, SRC_DURATION (container duration, captured here so
# verify() does not have to ffprobe the source a second time)
#
# SUBCOUNT counts only text subtitle streams mapped into the MP4.
probe() {
  local input=$1 out line idx codec type channels language title
  VCOUNT=0
  ACOUNT=0
  SUBCOUNT=0
  PIC_SUBS=0
  SRC_DURATION=""
  VCODECS=()
  ACODECS=()
  ACHANNELS=()
  ALANGS=()
  AUDIO_INDICES=()
  SUB_INDICES=()
  SLANGS=()
  STITLES=()
  VIDEO_MAPS=()
  AUDIO_MAPS=()
  SUB_MAPS=()

  out=$(ffprobe -v error \
    -show_entries stream=index,codec_name,codec_type,channels:stream_tags=language,title:stream_disposition=default:format=duration \
    -of compact=p=0:nk=0 -i "$input" 2>/dev/null) || return 1
  [[ -n "$out" ]] || return 1

  while IFS= read -r line; do
    idx=""
    codec=""
    type=""
    channels=""
    language=""
    title=""

    # The format section line (format|duration=...) carries the container duration;
    # stream lines never contain a duration key, so this cannot misfire.
    [[ $line =~ duration=([0-9.]+) ]] && SRC_DURATION=${BASH_REMATCH[1]}
    idx="" ; codec="" ; type="" ; channels="" ; language="" ; title=""
    [[ $line =~ index=([0-9]+) ]] && idx=${BASH_REMATCH[1]}
    [[ $line =~ codec_name=([^|]+) ]] && codec=${BASH_REMATCH[1]}
    [[ $line =~ codec_type=([a-z]+) ]] && type=${BASH_REMATCH[1]}
    [[ $line =~ channels=([0-9]+) ]] && channels=${BASH_REMATCH[1]}
    # ffprobe prints stream tags as tag:language=xxx / tag:title=... (some versions without prefix).
    [[ $line =~ language=([a-z0-9]+) ]] && language=${BASH_REMATCH[1]}
    [[ $line =~ title=([^|]+) ]] && title=${BASH_REMATCH[1]}

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
        ACHANNELS+=("${channels:-2}")
        ALANGS+=("${language:-und}")
        AUDIO_INDICES+=("$idx")
        AUDIO_MAPS+=(-map "0:$idx")
        ;;
      subtitle)
        case "$codec" in
          subrip|srt|ass|ssa|webvtt|mov_text|text)
            SUBCOUNT=$((SUBCOUNT + 1))
            SUB_INDICES+=("$idx")
            SLANGS+=("${language:-und}")
            STITLES+=("$title")
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
  local size avail floor
  size=$(stat -c %s -- "$1") || return 1
  avail=$(df --output=avail -B1 -- "$(dirname -- "$1")" 2>/dev/null |
    tail -n 1 | tr -d ' ')
  [[ "$avail" =~ ^[0-9]+$ ]] || return 1
  # Enough for this file plus 5%, and never less than the configured minimum free space.
  floor=$(( size + size / 20 + 104857600 ))
  if (( MIN_FREE_GB > 0 )); then
    local min_bytes=$(( MIN_FREE_GB * 1073741824 ))
    if (( min_bytes > floor )); then
      floor=$min_bytes
    fi
  fi
  (( avail > floor ))
}

# Sets REASON and returns nonzero if the output does not pass verification.
# The local variables keep probe() from overwriting the source-file probe data.
verify() {
  local expected_v=$1 expected_a=$2 expected_s=$3 src_duration=$4 src=$5 new=$6
  local d1 d2 new_size
  local VCOUNT ACOUNT SUBCOUNT PIC_SUBS ALANGS AUDIO_INDICES SUB_INDICES SLANGS STITLES SRC_DURATION
  local -a VCODECS ACODECS ACHANNELS VIDEO_MAPS AUDIO_MAPS SUB_MAPS

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

  # Durations come from the probes above (the source's was captured with its initial
  # probe, so the source is NOT probed again here); duration() is only the fallback
  # for files whose container section ffprobe skipped.
  d1="$src_duration"
  [[ -n "$d1" ]] || d1=$(duration "$src")
  d2="$SRC_DURATION"
  [[ -n "$d2" ]] || d2=$(duration "$new")
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

# ---- mobile Direct-Play compatibility (Jellyfin Android / ExoPlayer) ----

# Reads the top-level MP4 box list and reports where the moov index sits.
# Returns 0 when moov comes before mdat (faststart), 1 when it does not, and
# 2 when the scan fails (the caller treats that as needing repair).
mp4_is_faststart() {
  local f=$1 size pos ln t have_moov=-1 have_mdat=-1
  local -a hdr=()
  size=$(stat -c %s -- "$f") 2>/dev/null || return 2
  pos=0
  while (( pos + 8 <= size )); do
    read -r -a hdr <<< "$(dd if="$f" bs=1 skip=$pos count=8 2>/dev/null | od -An -tu1 -v | tr '\n' ' ')"
    (( ${#hdr[@]} == 8 )) || return 2
    ln=$(( (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3] ))
    t=$(dd if="$f" bs=1 skip=$((pos + 4)) count=4 2>/dev/null)
    if (( ln == 1 )); then
      ln=$(dd if="$f" bs=1 skip=$((pos + 8)) count=8 2>/dev/null | od -An -tu8 -v | tr -d ' ')
    elif (( ln == 0 )); then
      ln=$(( size - pos ))
    elif (( ln < 8 )); then
      return 2
    fi
    case "$t" in
      moov) [[ $have_moov -lt 0 ]] && have_moov=$pos ;;
      mdat) [[ $have_mdat -lt 0 ]] && have_mdat=$pos ;;
    esac
    pos=$(( pos + ln ))
  done
  (( have_moov >= 0 )) || return 2
  (( have_mdat < 0 || have_moov < have_mdat )) && return 0
  return 1
}

# Examines one existing .mp4 for mobile Direct-Play problems. Sets REPAIR_REASON
# (empty = nothing to do) and REPAIR_BLOCKED (1 = detected but not fixable
# losslessly). Checks: moov at the end, HEVC tagged hev1 instead of hvc1,
# data streams (bin_data), and Hi10P 10-bit H.264 (reported, not repairable).
mp4_repair_reason() {
  REPAIR_REASON=""
  REPAIR_BLOCKED=0
  local f=$1 vline vcodec vtag pix
  vline=$(ffprobe -v error -select_streams v -show_entries stream=codec_name,codec_tag_string,pix_fmt \
    -of csv=p=0 -- "$f" 2>/dev/null | head -n 1)
  vcodec=$(cut -d, -f1 <<< "$vline")
  vtag=$(cut -d, -f2 <<< "$vline")
  pix=$(cut -d, -f3 <<< "$vline")
  if [[ "$vcodec" == "h264" && "$pix" == "yuv420p10le" ]]; then
    REPAIR_REASON="video is Hi10P 10-bit H.264: no phone decodes it and it cannot be repaired losslessly (needs a re-encode)"
    REPAIR_BLOCKED=1
    return 0
  fi
  if [[ "$vcodec" == "hevc" && -n "$vtag" && "$vtag" != "hvc1" ]]; then
    REPAIR_REASON="HEVC is tagged '$vtag' (ExoPlayer needs 'hvc1')"
  fi
  if ! mp4_is_faststart "$f"; then
    [[ -n "$REPAIR_REASON" ]] && REPAIR_REASON+="; "
    REPAIR_REASON+="MP4 index (moov) is at the end of the file (Android spins before playback)"
  fi
  if ffprobe -v error -select_streams d -show_entries stream=index -of csv=p=0 -- "$f" 2>/dev/null | grep -q .; then
    [[ -n "$REPAIR_REASON" ]] && REPAIR_REASON+="; "
    REPAIR_REASON+="file contains data streams (bin_data)"
  fi
  return 0
}

converted=0
skipped=0
failed=0
total=0

mapfile -d '' -t SRC_FILES_RAW < <(
  # Repair mode looks at existing .mp4 files only (nothing to repair in an MKV
  # losslessly — the faststart/tag problems are MP4 container problems).
  if ! $REPAIR; then
    for base in "${FOLDERS[@]}"; do
      # -L follows symlinked episode files and library-view folders so files behind links
      # are converted too; the realpath dedup below keeps the same file from appearing twice.
      find -L "$base" -type f -iname '*.mkv' -print0 2>/dev/null
    done
  fi
  if $REMUX || $REPAIR; then
    for base in "${FOLDERS[@]}"; do
      while IFS= read -r -d '' mp4; do
        # MP4s that still have an MKV source are handled by the normal pass;
        # repair mode includes them so the mobile check can decide.
        if $REPAIR || ! find -L "$(dirname -- "$mp4")" -maxdepth 1 \
            -iname "$(basename -- "${mp4%.*}").mkv" -print -quit 2>/dev/null | grep -q .; then
          printf '%s\0' "$mp4"
        fi
      done < <(find -L "$base" -type f -iname '*.mp4' -print0 2>/dev/null)
    done
  fi
)

# Deduplicate by physical path: a symlinked tree pointing back into the real library
# would otherwise hand the same file to the loop twice.
declare -A SEEN_REAL=()
SRC_FILES=()
for src_file in "${SRC_FILES_RAW[@]}"; do
  real=$(realpath -- "$src_file" 2>/dev/null) || real=$src_file
  [[ -n "${SEEN_REAL[$real]:-}" ]] && continue
  SEEN_REAL[$real]=1
  SRC_FILES+=("$src_file")
done
unset SRC_FILES_RAW SEEN_REAL real src_file
for src in "${SRC_FILES[@]}"; do
  total=$((total + 1))
  # MP4 sources (remux-existing mode) replace themselves; MKV sources get a new MP4.
  if [[ "${src,,}" == *.mp4 ]]; then
    INPLACE=true
    out="$src"
  else
    INPLACE=false
    out="${src%.*}.mp4"
  fi
  tmp="${out}${PART_SUFFIX}"

  echo
  echo "File: $src"

  # Cheap skip checks first: during playback there is no point waiting for files
  # that would be skipped anyway.
  if [[ -e "$out" || -L "$out" ]] && ! $REDO && ! $INPLACE; then
    echo "  Skipping: destination already exists: $out"
    report "$src" "destination already exists: $out"
    skipped=$((skipped + 1))
    continue
  fi
  if [[ -e "$tmp" || -L "$tmp" ]]; then
    echo "  Skipping: temporary file already exists: $tmp"
    report "$src" "leftover temporary file exists: $tmp"
    skipped=$((skipped + 1))
    continue
  fi

  # Repair mode: check an existing .mp4 for mobile Direct-Play problems (moov at
  # the end, hev1 tag, data streams) before anything else; already-clean files
  # skip instantly. Hi10P is reported as not losslessly repairable.
  if $REPAIR && $INPLACE; then
    mp4_repair_reason "$src"
    if [[ -z "$REPAIR_REASON" ]]; then
      echo "  Skipping: already mobile-friendly (faststart + hvc1, no data streams)."
      skipped=$((skipped + 1))
      continue
    fi
    if (( REPAIR_BLOCKED )); then
      echo "  Skipping: $REPAIR_REASON"
      report "$src" "$REPAIR_REASON"
      skipped=$((skipped + 1))
      continue
    fi
    echo "  Mobile repair: $REPAIR_REASON"
  fi

  # Read-only locations (network shares, write-protected folders) can never receive the
  # temporary file, so test that once with a touch instead of failing inside ffmpeg.
  src_dir=$(dirname -- "$src")
  if ! touch "$src_dir/.ep_write_test" 2>/dev/null; then
    echo "  Skipping: folder is read-only or not writable: $src_dir"
    report "$src" "folder is read-only or not writable: $src_dir"
    skipped=$((skipped + 1))
    continue
  fi
  rm -f -- "$src_dir/.ep_write_test"

  if ! probe "$src"; then
    echo "  Skipping: could not read streams with ffprobe."
    report "$src" "ffprobe could not read the file (corrupt or unsupported container)"
    skipped=$((skipped + 1))
    continue
  fi

  if (( VCOUNT + ACOUNT == 0 )); then
    echo "  Skipping: no supported video or audio streams found."
    skipped=$((skipped + 1))
    continue
  fi

  # ---- per-show track selection (Episode Tools Shows page) ----
  # Repair mode keeps the file exactly as it is: a lossless re-tag/re-mux must not
  # silently drop tracks or change dispositions.
  if $REPAIR; then
    SUBPREF=""
    AUDPREF=""
    AUDPREF2=""
  fi
  show=$(show_name_for "$src")
  get_show_pref "$show"
  set_default_disposition=false

  if [[ -n "$SUBPREF" ]]; then
    if [[ "$SUBPREF" == "none" ]]; then
      echo "  Show preference ($show): dropping subtitles."
      SUB_MAPS=(); SUB_INDICES=(); SLANGS=(); STITLES=(); SUBCOUNT=0
      set_default_disposition=true
    else
      orig_sub_count=${#SUB_INDICES[@]}
      new_sub_maps=(); new_sub_indices=(); new_sub_langs=(); new_sub_titles=(); kept=0
      for i in "${!SUB_INDICES[@]}"; do
        if lang_matches "${SLANGS[$i]}" "$SUBPREF"; then
          new_sub_maps+=(-map "0:${SUB_INDICES[$i]}")
          new_sub_indices+=("${SUB_INDICES[$i]}")
          new_sub_langs+=("${SLANGS[$i]}")
          new_sub_titles+=("${STITLES[$i]:-}")
          kept=$((kept + 1))
        fi
      done
      if (( kept > 0 )); then
        SUB_MAPS=("${new_sub_maps[@]}")
        SUB_INDICES=("${new_sub_indices[@]}")
        SLANGS=("${new_sub_langs[@]}")
        STITLES=("${new_sub_titles[@]}")
        SUBCOUNT=$kept
        echo "  Show preference ($show): keeping only '$SUBPREF' subtitles ($kept of $orig_sub_count found)."
      else
        # Safety: a filter that matches nothing (e.g. all tracks tagged 'und') must never
        # silently produce a file without subtitles.
        echo "  Show preference ($show): no '$SUBPREF' subtitle track found; keeping all subtitles."
      fi
      set_default_disposition=true
    fi
  fi

  # ---- drop signs/songs/lyrics subtitle tracks (keep dialogue) ----
  # Word-boundary match: "English Subtitles" contains "titles" as a substring but must NOT be
  # treated as a titles track; only standalone words count.
  if $DROP_SIGNS_SONGS && ! $REPAIR && (( ${#SUB_INDICES[@]} > 1 )); then
    kept_sub_maps=(); kept_sub_indices=(); kept_sub_langs=(); kept_sub_titles=()
    dropped=""
    for i in "${!SUB_INDICES[@]}"; do
      t="${STITLES[$i]:-}"
      padded=" ${t,,} "
      if [[ "$padded" =~ [^a-z](signs|songs|song|lyrics|karaoke|titles|credits)[^a-z] ]]; then
        dropped+=" '${t}'"
      else
        kept_sub_maps+=(-map "0:${SUB_INDICES[$i]}")
        kept_sub_indices+=("${SUB_INDICES[$i]}")
        kept_sub_langs+=("${SLANGS[$i]}")
        kept_sub_titles+=("$t")
      fi
    done
    if [[ -n "$dropped" ]] && (( ${#kept_sub_maps[@]} > 0 )); then
      SUB_MAPS=("${kept_sub_maps[@]}")
      SUB_INDICES=("${kept_sub_indices[@]}")
      SLANGS=("${kept_sub_langs[@]}")
      STITLES=("${kept_sub_titles[@]}")
      SUBCOUNT=${#SUB_INDICES[@]}
      echo "  Dropping signs/songs subtitle track(s):$dropped"
    elif [[ -n "$dropped" ]]; then
      echo "  Note: only signs/songs subtitle track(s) exist; keeping them."
    fi
  fi

  if [[ -n "$AUDPREF" ]]; then
    pick_audio() {  # $1 = preferred language; sets the new_* arrays when matches exist
      new_audio_maps=(); new_acodecs=(); new_achannels=(); kept=0
      for i in "${!AUDIO_INDICES[@]}"; do
        if lang_matches "${ALANGS[$i]}" "$1"; then
          new_audio_maps+=(-map "0:${AUDIO_INDICES[$i]}")
          new_acodecs+=("${ACODECS[$i]}")
          new_achannels+=("${ACHANNELS[$i]}")
          kept=$((kept + 1))
        fi
      done
    }

    pick_audio "$AUDPREF"
    if (( kept > 0 )); then
      AUDIO_MAPS=("${new_audio_maps[@]}")
      ACODECS=("${new_acodecs[@]}")
      ACHANNELS=("${new_achannels[@]}")
      ACOUNT=$kept
      echo "  Show preference ($show): keeping only '$AUDPREF' audio ($kept track(s))."
      set_default_disposition=true
    elif [[ -n "$AUDPREF2" ]]; then
      pick_audio "$AUDPREF2"
      if (( kept > 0 )); then
        AUDIO_MAPS=("${new_audio_maps[@]}")
        ACODECS=("${new_acodecs[@]}")
        ACHANNELS=("${new_achannels[@]}")
        ACOUNT=$kept
        echo "  Show preference ($show): no '$AUDPREF' audio; falling back to '$AUDPREF2' ($kept track(s))."
        set_default_disposition=true
      else
        echo "  Show preference ($show): no '$AUDPREF' or '$AUDPREF2' audio found; keeping all audio."
      fi
    else
      echo "  Show preference ($show): no '$AUDPREF' audio track found; keeping all audio."
    fi
  fi

# ---- drop garbage subtitle tracks (numbers/ads/spam content) ----
# Uses python3 for the content analysis; without python3 the feature is skipped silently.
# Every subtitle track is extracted in ONE ffmpeg pass (one read of the file instead of
# one per track), each into its own output file.
if $DROP_GARBAGE_SUBS && ! $REPAIR && command -v python3 >/dev/null 2>&1 && (( ${#SUB_INDICES[@]} > 1 )); then
  GARBAGE_DIR=$(mktemp -d "${TMPDIR:-/tmp}/epgarbage.XXXXXX")
  garbage_args=()
  for i in "${!SUB_INDICES[@]}"; do
    # ${SUB_INDICES[$i]} is the real stream index - "0:s:$i" would probe the wrong
    # track whenever an earlier subtitle was filtered out by preferences.
    garbage_args+=(-map "0:${SUB_INDICES[$i]}" -f srt "$GARBAGE_DIR/sub$i.srt")
  done
  # Pathological fansub tracks (one cue per letter, tens of thousands of cues scattered
  # across a big file) make SRT extraction crawl. Cap the whole pass, force-kill on
  # expiry (timeout -k: ffmpeg can ignore a plain SIGTERM while stuck in a loop), and
  # keep every track when the cap trips.
  timeout -k 15 60 ffmpeg -v error -y -i "$src" "${garbage_args[@]}" 2>/dev/null
  rc=$?
  if (( rc == 124 || rc == 137 )); then
    echo "  Note: subtitle extraction timed out - skipping garbage analysis, all tracks kept."
    rm -rf -- "$GARBAGE_DIR"
    GARBAGE_DIR=""
  else
    kept_sub_maps=(); kept_sub_indices=(); kept_sub_langs=(); kept_sub_titles=()
    dropped=""
    for i in "${!SUB_INDICES[@]}"; do
      garbage_tmp="$GARBAGE_DIR/sub$i.srt"
      if [[ -s "$garbage_tmp" ]]; then
        ratio=$(python3 - "$garbage_tmp" <<'PYEOF'
import re, sys
try:
    text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
except OSError:
    print("ERR"); raise SystemExit
blocks = re.split(r"\n\s*\n", text)
cues = 0
bad = 0
AD = re.compile(r"opensubtitles|subtitles? (by|from)|addic7ed|rarbg|sync (by|and corrected)|release.?group|_subs_|subs please", re.I)
for block in blocks:
    lines = [l for l in block.splitlines() if l.strip()]
    if len(lines) < 2:
        continue
    t_idx = next((i for i, l in enumerate(lines) if "-->" in l), None)
    if t_idx is None:
        continue
    body = " ".join(lines[t_idx + 1:])
    if not body.strip():
        continue
    cues += 1
    garbage = False
    if re.fullmatch(r"[\d\s.:,%-]+", body):
        garbage = True                      # pure numbers/punctuation
    elif AD.search(body):
        garbage = True                      # ad/spam lines
    else:
        alpha = sum(c.isalpha() for c in body)
        if len(body) >= 8 and alpha / max(len(body), 1) < 0.3:
            garbage = True                  # mostly non-letters: OCR junk
        else:
            words = [w for w in re.findall(r"[A-Za-z]{4,}", body)]
            if len(words) >= 3 and sum(1 for w in words if not re.search(r"[aeiouyAEIOUY]", w)) / len(words) > 0.6:
                garbage = True              # long vowel-less words: OCR gibberish
    if garbage:
        bad += 1
print(f"{bad / cues:.2f}" if cues >= 10 else "OK")
PYEOF
)
        if [[ "$ratio" =~ ^0?\.[3-9]$|^1$|^0\.[3-9][0-9]?$ ]]; then
          dropped+=" '${STITLES[$i]:-track $i}' (${ratio} garbage)"
        else
          kept_sub_maps+=(-map "0:${SUB_INDICES[$i]}")
          kept_sub_indices+=("${SUB_INDICES[$i]}")
          kept_sub_langs+=("${SLANGS[$i]}")
          kept_sub_titles+=("${STITLES[$i]:-}")
        fi
      else
        # Track could not be extracted (image-based, or ffmpeg failed on it): keep it.
        kept_sub_maps+=(-map "0:${SUB_INDICES[$i]}")
        kept_sub_indices+=("${SUB_INDICES[$i]}")
        kept_sub_langs+=("${SLANGS[$i]}")
        kept_sub_titles+=("${STITLES[$i]:-}")
      fi
    done
    rm -rf -- "$GARBAGE_DIR"
    GARBAGE_DIR=""
    if [[ -n "$dropped" ]] && (( ${#kept_sub_maps[@]} > 0 )); then
      SUB_MAPS=("${kept_sub_maps[@]}")
      SUB_INDICES=("${kept_sub_indices[@]}")
      SLANGS=("${kept_sub_langs[@]}")
      STITLES=("${kept_sub_titles[@]}")
      SUBCOUNT=${#SUB_INDICES[@]}
      echo "  Dropping garbage subtitle track(s):$dropped"
    elif [[ -n "$dropped" ]]; then
      echo "  Note: every subtitle track looks like garbage; keeping them all."
    fi
  fi
fi

  unsafe=""
  for codec in "${VCODECS[@]}"; do
    if ! is_safe_codec video "$codec"; then
      unsafe+=" video:$codec"
    fi
  done
  # Audio that MP4 handles badly (DTS, TrueHD, FLAC...). With --audio-to-aac these tracks are
  # re-encoded to AAC (output audio stream number i is input audio stream number i).
  unsafe_audio=""
  audio_args=()
  for i in "${!ACODECS[@]}"; do
    codec=${ACODECS[$i]}
    if ! is_safe_codec audio "$codec"; then
      unsafe_audio+=" audio:$codec"
      # Repair mode never re-encodes: the goal is a minimal, lossless change.
      if $AUDIO_TO_AAC && ! $REPAIR; then
        channels=${ACHANNELS[$i]:-2}
        if (( channels <= 2 )); then
          bitrate=192k
        elif (( channels <= 6 )); then
          bitrate=448k
        else
          bitrate=640k
        fi
        audio_args+=("-c:a:$i" aac "-b:a:$i" "$bitrate")
      fi
    fi
  done
  if ! $AUDIO_TO_AAC; then
    unsafe+="$unsafe_audio"
  fi

  # When a per-show preference exists, flag the first audio and first subtitle
  # track as the default for players.
  disp_args=()
  if $set_default_disposition; then    if (( ${#AUDIO_MAPS[@]} > 0 )); then
      disp_args+=(-disposition:a:0 default)
    fi
    if (( ${#SUB_MAPS[@]} > 0 )); then
      disp_args+=(-disposition:s:0 default)
    fi
  fi

  if [[ -n "$unsafe" ]] && ! $FORCE && ! $REPAIR; then
    echo "  Skipping: unusual MP4 codec(s):${unsafe}"
    echo "  Use --force to attempt this file."
    report "$src" "unusual MP4 codec(s):${unsafe} (use --force to attempt)"
    skipped=$((skipped + 1))
    continue
  fi

  if (( PIC_SUBS > 0 )); then
    echo "  Note: omitting $PIC_SUBS unsupported subtitle stream(s)."
  fi

  if ! $APPLY; then
    echo "  Would convert: $VCOUNT video, $ACOUNT audio, $SUBCOUNT text subtitle stream(s)"
    if [[ "${src,,}" == *.mp4 ]]; then
      echo "  Would re-mux: existing MP4 (re-apply settings, clean subtitles, replace after verification)"
    fi
    [[ -n "$unsafe" ]] && echo "  Warning: --force would be required for:${unsafe}"
    [[ -n "$unsafe_audio" ]] && $AUDIO_TO_AAC && echo "  Audio would be re-encoded to AAC:${unsafe_audio}"
    continue
  fi

  if ! enough_space "$src"; then
    echo "  Skipping: not enough free space for a safe conversion (minimum ${MIN_FREE_GB} GB free required)."
    report "$src" "not enough free space for a safe conversion (minimum ${MIN_FREE_GB} GB free required)"
    skipped=$((skipped + 1))
    continue
  fi

  source_identity=$(stat -c '%d:%i:%s:%y' -- "$src") || {
    echo "  Skipping: could not identify source file."
    skipped=$((skipped + 1))
    continue
  }

  # Clean the kept subtitle streams: strip style tags and merge the micro-cues of
  # typewriter/karaoke tracks, otherwise the conversion to mov_text flashes literal
  # garbage (e.g. "{=63}") and one cue per letter strobes on screen.
  declare -A CLEAN_MAP=()
  CLEANED_INPUTS=()
  if $APPLY && ! $REPAIR && (( ${#SUB_INDICES[@]} > 0 )); then
    clean_py="$(dirname -- "$0")/clean_subs.py"
    if [[ -f "$clean_py" ]] && command -v python3 >/dev/null 2>&1; then
      CLEAN_DIR=$(mktemp -d "${TMPDIR:-/tmp}/epclean.XXXXXX")
      while IFS=$'\t' read -r cidx cpath; do
        [[ -n "${cpath:-}" ]] || continue
        CLEAN_MAP["$cidx"]="$cpath"
      done < <(python3 "$clean_py" "$src" "$CLEAN_DIR" "${SUB_INDICES[@]}" 2>/dev/null)
      if (( ${#CLEAN_MAP[@]} > 0 )); then
        echo "  Cleaned ${#CLEAN_MAP[@]} subtitle stream(s) (style tags stripped, micro-cues merged)."
      fi
    fi
  fi

  maps=("${VIDEO_MAPS[@]}" "${AUDIO_MAPS[@]}")
  extra_input=1
  sout=0
  for i in "${SUB_INDICES[@]}"; do
    cleaned="${CLEAN_MAP[$i]:-}"
    if [[ -n "$cleaned" ]]; then
      CLEANED_INPUTS+=(-i "$cleaned")
      maps+=(-map "${extra_input}:0")
      extra_input=$((extra_input + 1))
    else
      maps+=(-map "0:${i}")
    fi
    slang="${SLANGS[$sout]:-}"
    if [[ -n "$slang" && "$slang" != "und" ]]; then
      maps+=(-metadata:s:s:$sout "language=$slang")
    fi
    sout=$((sout + 1))
  done
  CURRENT_TMP=$tmp

  movflags=()
  # MP4-to-MP4 remuxes (repair / remux-existing) always get faststart: it is the
  # whole point of repairing for mobile, and the file is rewritten anyway.
  # Auto-repair mode forces faststart on brand-new conversions too: the whole point
  # of the toggle is that no new file can be born without a mobile-friendly index,
  # even when the faststart config option itself is off.
  if $FASTSTART || $INPLACE || $AUTO_REPAIR; then
    movflags=(-movflags +faststart)
  fi

  # HEVC video must be tagged hvc1 in MP4: ffmpeg writes hev1 by default, which
  # ExoPlayer (Jellyfin Android) cannot Direct Play. -fflags +genpts regenerates
  # missing/non-monotonic PTS that would otherwise stall stream-copy remuxes at
  # stream startup on some players.
  tag_args=()
  for i in "${!VCODECS[@]}"; do
    if [[ "${VCODECS[$i]}" == "hevc" ]]; then
      tag_args+=("-tag:v:$i" "hvc1")
    fi
  done

  # Only now, when the file will actually be converted, wait for the playback
  # pause flag to clear.
  wait_for_playback

  if [[ -n "$unsafe_audio" ]] && $AUDIO_TO_AAC; then
    echo "  Re-encoding audio to AAC:${unsafe_audio}"
  fi
  echo "  Converting..."
  # -map_chapters -1: never carry MKV chapter tracks into the MP4 — they become
  # the bin_data streams that ExoPlayer refuses to play (the 541-file sweep bug).
  # NOTE: keep that comment here and NOT inside the line-continued command below —
  # a '#' inside a continued command terminates it early and reorders the arguments
  # (bash parses the rest of the command as separate programs).
  if ! "${PRIO[@]}" ffmpeg -nostdin -hide_banner -loglevel warning \
      -fflags +genpts \
      -i "$src" \
      "${CLEANED_INPUTS[@]}" \
      "${maps[@]}" \
      -map_metadata 0 -map_chapters -1 \
      -c copy -c:s mov_text \
      "${audio_args[@]}" \
      "${disp_args[@]}" \
      "${tag_args[@]}" \
      "${movflags[@]}" -f mp4 "$tmp"; then
    echo "  Failed: ffmpeg could not create the MP4."
    report "$src" "ffmpeg failed to create the MP4 (see Dashboard > Logs for the ffmpeg error)"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  if ! verify "$VCOUNT" "$ACOUNT" "$SUBCOUNT" "$SRC_DURATION" "$src" "$tmp"; then
    echo "  Failed verification: $REASON"
    report "$src" "output failed verification: $REASON"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  # -n prevents overwriting a destination created by another process.
  # Re-muxing in place replaces the source itself (it IS the destination).
  if $REDO || $INPLACE; then
    if ! mv -fT -- "$tmp" "$out"; then
      echo "  Failed: could not replace the existing .mp4."
      report "$src" "could not replace the existing .mp4"
      rm -f -- "$tmp"
      CURRENT_TMP=""
      failed=$((failed + 1))
      continue
    fi
    echo "  Replaced: $out"
  elif ! mv -nT -- "$tmp" "$out" || [[ -e "$tmp" ]]; then
    echo "  Failed: could not move verified output without overwriting."
    report "$src" "could not move verified output without overwriting"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi
  CURRENT_TMP=""

  echo "  Created and verified: $out"
  converted=$((converted + 1))

  # Auto mobile repair (Episode Tools "auto-repair new MP4s"): the brand-new MP4 is
  # probed exactly like repair mode checks existing files; if it still would fail
  # Direct Play on mobile (moov at the end, hev1 tag, bin_data), it is losslessly
  # remuxed in place. Every track is kept, nothing is re-encoded, dispositions are
  # carried through untouched. Hi10P cannot be fixed losslessly and is only reported.
  if $AUTO_REPAIR && ! $INPLACE; then
    mp4_repair_reason "$out"
    if [[ -n "$REPAIR_REASON" ]]; then
      if (( REPAIR_BLOCKED )); then
        echo "  Auto-repair: cannot fix losslessly — $REPAIR_REASON"
        report "$out" "auto-repair blocked: $REPAIR_REASON"
      else
        echo "  Auto-repair: $REPAIR_REASON"
        rtmp="${out}${PART_SUFFIX}"
        if probe "$out" && (( VCOUNT + ACOUNT > 0 )); then
          rmaps=("${VIDEO_MAPS[@]}" "${AUDIO_MAPS[@]}")
          rsout=0
          for i in "${SUB_INDICES[@]}"; do
            rmaps+=("-map" "0:${i}")
            rslang="${SLANGS[$rsout]:-}"
            if [[ -n "$rslang" && "$rslang" != "und" ]]; then
              rmaps+=("-metadata:s:s:$rsout" "language=$rslang")
            fi
            rsout=$((rsout + 1))
          done
          rtags=()
          for i in "${!VCODECS[@]}"; do
            if [[ "${VCODECS[$i]}" == "hevc" ]]; then
              rtags+=("-tag:v:$i" "hvc1")
            fi
          done
          # Same verify-then-atomic-replace pattern as the main conversion: a failed
          # repair never damages the file it tried to fix.
          if "${PRIO[@]}" ffmpeg -nostdin -hide_banner -loglevel warning \
              -fflags +genpts \
              -i "$out" \
              "${rmaps[@]}" \
              -map_metadata 0 -map_chapters -1 \
              -c copy \
              "${rtags[@]}" \
              -movflags +faststart -f mp4 "$rtmp" \
            && verify "$VCOUNT" "$ACOUNT" "$SUBCOUNT" "$SRC_DURATION" "$out" "$rtmp" \
            && mv -fT -- "$rtmp" "$out"; then
            echo "  Auto-repaired: $out"
          else
            echo "  Warning: auto-repair failed; the file is kept exactly as it was."
            report "$out" "auto-repair failed ($REPAIR_REASON); file kept unchanged"
            rm -f -- "$rtmp"
          fi
        else
          echo "  Warning: auto-repair could not probe the new MP4; leaving it unchanged."
        fi
      fi
    fi
  fi

  # Never delete in remux mode: there $src is the freshly replaced output.
  if $DELETE && ! $INPLACE; then
    current_identity=$(stat -c '%d:%i:%s:%y' -- "$src" 2>/dev/null || true)
    if [[ "$current_identity" != "$source_identity" ]]; then
      echo "  Kept original: it changed during conversion."
    elif rm -- "$src"; then
      echo "  Deleted original: $src"
    else
      echo "  Warning: could not delete original: $src"
    fi
  fi
done

echo
echo "Finished: $total file(s) found; $converted converted, $skipped skipped, $failed failed."
if ! $APPLY; then
  echo "Dry run only. Use --apply to convert files."
fi

if (( failed > 0 )); then
  exit 1
fi
