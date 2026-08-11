#!/usr/bin/env bash
set -euo pipefail

test_dir="$(mktemp -d /tmp/kadr-export-smoke.XXXXXX)"
cleanup() {
  if [[ -n "${test_dir:-}" && -d "$test_dir" && "$test_dir" == /tmp/kadr-export-smoke.* ]]; then
    rm -rf -- "$test_dir"
  fi
}
trap cleanup EXIT

ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=640x360:rate=30:duration=1.4" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=1.4" \
  -map 0:v:0 -map 1:a:0 \
  -vf "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=30,format=yuv420p" \
  -af "aresample=48000,volume=1,apad" -t 1.4 \
  -c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p \
  -c:a aac -b:a 192k -ar 48000 -ac 2 "$test_dir/segment-00000.mp4"

ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "color=c=0x6f46c1:size=800x600:rate=30:duration=1.1" \
  -f lavfi -t 1.1 -i "anullsrc=channel_layout=stereo:sample_rate=48000" \
  -map 0:v:0 -map 1:a:0 \
  -vf "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=30,format=yuv420p" \
  -af "aresample=48000,volume=1,apad" -t 1.1 \
  -c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p \
  -c:a aac -b:a 192k -ar 48000 -ac 2 "$test_dir/segment-00001.mp4"

printf "file '%s'\nfile '%s'\n" \
  "$test_dir/segment-00000.mp4" \
  "$test_dir/segment-00001.mp4" > "$test_dir/concat.txt"

ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i "$test_dir/concat.txt" \
  -c copy -movflags +faststart "$test_dir/base.mp4"

ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=880:sample_rate=48000:duration=1" \
  -c:a pcm_s16le "$test_dir/overlay.wav"

ffmpeg -hide_banner -loglevel error -y \
  -i "$test_dir/base.mp4" -ss 0 -t 1 -i "$test_dir/overlay.wav" \
  -filter_complex "[0:a]aresample=48000[baseaudio];[1:a:0]aresample=48000,atrim=0:1,asetpts=PTS-STARTPTS,volume=0.35,adelay=600|600[audio0];[baseaudio][audio0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[mixed]" \
  -map 0:v:0 -map "[mixed]" -c:v copy -c:a aac -b:a 192k -ar 48000 -ac 2 \
  -t 2.5 -movflags +faststart "$test_dir/result.mp4"

duration="$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "$test_dir/result.mp4")"
video_streams="$(ffprobe -v error -select_streams v -show_entries stream=index -of csv=p=0 "$test_dir/result.mp4" | wc -l)"
audio_streams="$(ffprobe -v error -select_streams a -show_entries stream=index -of csv=p=0 "$test_dir/result.mp4" | wc -l)"

awk -v value="$duration" 'BEGIN { if (value < 2.45 || value > 2.60) exit 1 }'
[[ "$video_streams" -eq 1 ]]
[[ "$audio_streams" -eq 1 ]]

echo "Kadr export smoke test passed: duration=${duration}s, video=${video_streams}, audio=${audio_streams}"

