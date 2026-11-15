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
REDO=false
MIN_FREE_GB=10
REPORT_FILE=""
PREFS_FILE=""
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
  --drop-signs-songs   Drop subtitle tracks whose title says signs/songs/lyrics/karaoke
                       (typical anime tracks) as long as another dialogue track exists.
  --redo               Also convert files whose .mp4 already exists and REPLACE the
                       .mp4 after verification. Use after changing preferences/settings.
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
    --redo) REDO=true ;;
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

# ---- per-show subtitle/audio preferences (Episode Tools Shows page) ----
PREF_SHOW=(); PREF_SUB=(); PREF_AUD=()
if [[ -n "$PREFS_FILE" && -f "$PREFS_FILE" ]]; then
  while IFS=$'\t' read -r s sub aud; do
    [[ -n "$s" ]] || continue
    PREF_SHOW+=("$s"); PREF_SUB+=("${sub:-}"); PREF_AUD+=("${aud:-}")
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

# get_show_pref <show-name>: sets SUBPREF and AUDPREF (empty = keep all).
get_show_pref() {
  SUBPREF=""
  AUDPREF=""
  local want="${1,,}" i
  for i in "${!PREF_SHOW[@]}"; do
    if [[ "${PREF_SHOW[$i],,}" == "$want" ]]; then
      SUBPREF="${PREF_SUB[$i]:-}"
      AUDPREF="${PREF_AUD[$i]:-}"
      return 0
    fi
  done
}

# show_name_for <file-path>: the show is the first folder under the scanned folder.
show_name_for() {
  local p=$1 base rel
  for base in "${FOLDERS[@]}"; do
    base="${base%/}/"
    if [[ "$p/" == "$base"* ]]; then
      rel="${p#"$base"}"
      printf '%s' "${rel%%/*}"
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
cleanup() {
  if [[ -n "$CURRENT_TMP" ]]; then
    rm -f -- "$CURRENT_TMP"
  fi
}
trap cleanup EXIT
trap 'echo; echo "Interrupted; cleaning up the unfinished file."; exit 130' INT TERM HUP

# Populated by probe():
# VCOUNT, VCODECS[], VIDEO_MAPS[], ACOUNT, ACODECS[], AUDIO_MAPS[], ALANGS[],
# SUBCOUNT, SUB_MAPS[], PIC_SUBS
#
# SUBCOUNT counts only text subtitle streams mapped into the MP4.
probe() {
  local input=$1 out line idx codec type channels language title
  VCOUNT=0
  ACOUNT=0
  SUBCOUNT=0
  PIC_SUBS=0
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
    -show_entries stream=index,codec_name,codec_type,channels:stream_tags=language,title:stream_disposition=default \
    -of compact=p=0:nk=0 -i "$input" 2>/dev/null) || return 1
  [[ -n "$out" ]] || return 1

  while IFS= read -r line; do
    idx=""
    codec=""
    type=""
    channels=""
    language=""
    title=""

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
  local expected_v=$1 expected_a=$2 expected_s=$3 src=$4 new=$5
  local d1 d2 new_size
  local VCOUNT ACOUNT SUBCOUNT PIC_SUBS ALANGS AUDIO_INDICES SUB_INDICES SLANGS STITLES
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

  if [[ -e "$out" || -L "$out" ]] && ! $REDO; then
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
  if $DROP_SIGNS_SONGS && (( ${#SUB_INDICES[@]} > 1 )); then
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
    new_audio_maps=(); new_acodecs=(); new_achannels=(); new_unsafe=""; kept=0
    for i in "${!AUDIO_INDICES[@]}"; do
      if lang_matches "${ALANGS[$i]}" "$AUDPREF"; then
        new_audio_maps+=(-map "0:${AUDIO_INDICES[$i]}")
        new_acodecs+=("${ACODECS[$i]}")
        new_achannels+=("${ACHANNELS[$i]}")
        kept=$((kept + 1))
      fi
    done
    if (( kept > 0 )); then
      AUDIO_MAPS=("${new_audio_maps[@]}")
      ACODECS=("${new_acodecs[@]}")
      ACHANNELS=("${new_achannels[@]}")
      ACOUNT=$kept
      echo "  Show preference ($show): keeping only '$AUDPREF' audio ($kept track(s))."
      set_default_disposition=true
    else
      echo "  Show preference ($show): no '$AUDPREF' audio track found; keeping all audio."
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
      if $AUDIO_TO_AAC; then
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
  if $set_default_disposition; then
    if (( ${#AUDIO_MAPS[@]} > 0 )); then
      disp_args+=(-disposition:a:0 default)
    fi
    if (( ${#SUB_MAPS[@]} > 0 )); then
      disp_args+=(-disposition:s:0 default)
    fi
  fi

  if [[ -n "$unsafe" ]] && ! $FORCE; then
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

  maps=("${VIDEO_MAPS[@]}" "${AUDIO_MAPS[@]}" "${SUB_MAPS[@]}")
  CURRENT_TMP=$tmp

  movflags=()
  if $FASTSTART; then
    movflags=(-movflags +faststart)
  fi

  if [[ -n "$unsafe_audio" ]] && $AUDIO_TO_AAC; then
    echo "  Re-encoding audio to AAC:${unsafe_audio}"
  fi
  echo "  Converting..."
  if ! "${PRIO[@]}" ffmpeg -nostdin -hide_banner -loglevel warning \
      -i "$src" \
      "${maps[@]}" \
      -map_metadata 0 -map_chapters 0 \
      -c copy -c:s mov_text \
      "${audio_args[@]}" \
      "${disp_args[@]}" \
      "${movflags[@]}" -f mp4 "$tmp"; then
    echo "  Failed: ffmpeg could not create the MP4."
    report "$src" "ffmpeg failed to create the MP4 (see Dashboard > Logs for the ffmpeg error)"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  if ! verify "$VCOUNT" "$ACOUNT" "$SUBCOUNT" "$src" "$tmp"; then
    echo "  Failed verification: $REASON"
    report "$src" "output failed verification: $REASON"
    rm -f -- "$tmp"
    CURRENT_TMP=""
    failed=$((failed + 1))
    continue
  fi

  # -n prevents overwriting a destination created by another process.
  if $REDO; then
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
done < <(for base in "${FOLDERS[@]}"; do
  find "$base" -type f -iname '*.mkv' -print0 2>/dev/null
done)

echo
echo "Finished: $total file(s) found; $converted converted, $skipped skipped, $failed failed."
if ! $APPLY; then
  echo "Dry run only. Use --apply to convert files."
fi

if (( failed > 0 )); then
  exit 1
fi
