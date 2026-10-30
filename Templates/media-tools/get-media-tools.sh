#!/usr/bin/env bash
# Fetch yt-dlp and ffmpeg for the KMH video server, into a Tools folder KMH already searches.
#
#   ./get-media-tools.sh                    # this machine's architecture, Tools/ beside the script
#   ./get-media-tools.sh linux-arm64        # fetch for another machine, then copy Tools/ across
#   DEST=/srv/kmh ./get-media-tools.sh      # write into /srv/kmh/Tools
#
# No net on the server? Run this on any machine that has one and copy the whole Tools folder to the
# server, beside KMHServerAddon. Nothing here phones home at runtime.
set -euo pipefail

RID="${1:-}"
DEST="${DEST:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
FORCE="${FORCE:-0}"

if [ -z "$RID" ]; then
    case "$(uname -m)" in
        x86_64|amd64)   RID=linux-x64 ;;
        aarch64|arm64)  RID=linux-arm64 ;;
        armv7l|armhf)   RID=linux-arm ;;
        *) echo "unrecognised machine $(uname -m) - pass a rid: linux-x64 | linux-arm64 | linux-arm" >&2; exit 2 ;;
    esac
    echo "[tools] detected $RID"
fi

case "$RID" in
    linux-x64)   YT_URL=https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux
                 FF_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-lgpl.tar.xz ;;
    linux-arm64) YT_URL=https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64
                 FF_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-lgpl.tar.xz ;;
    linux-arm)   YT_URL=https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_armv7l.zip
                 FF_URL=https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-armhf-static.tar.xz ;;
    *) echo "this script is for Linux; on Windows use get-media-tools.ps1" >&2; exit 2 ;;
esac

# The release ships this script inside Tools/, so DEST is already that folder and adding another 'Tools'
# buried the binaries a level below everything these messages promise.
if [ "$(basename "$DEST")" = "Tools" ]; then TOOLS="$DEST"; else TOOLS="$DEST/Tools"; fi
mkdir -p "$TOOLS"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# LGPL, not GPL, where one exists: a GPL build puts its own redistribution terms on whoever the server is
# handed to next. Nobody publishes an LGPL 32-bit ARM build, so linux-arm gets johnvansickle's GPL one.
YT_SUMS=https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS
if [ "$RID" = "linux-arm" ]; then FF_SUMS="$FF_URL.md5"; FF_ALGO=md5; else FF_SUMS=https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256; FF_ALGO=sha256; fi

get() {  # get <url> <out>
    if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
    else echo "need curl or wget" >&2; exit 1; fi
}

# The server runs these binaries, so each is checked against its publisher's checksum list from the same release.
fetch() {  # fetch <url> <out> <sums-url> <sha256|md5>
    local leaf; leaf="$(basename "$1")"
    echo "[tools] downloading $leaf"
    get "$1" "$2"
    local n; n=$(stat -c %s "$2" 2>/dev/null || stat -f %z "$2")
    if [ "$n" -lt 102400 ]; then echo "$2 came back only $n bytes - the download did not complete" >&2; exit 1; fi
    get "$3" "$TMP/sums"
    local want got
    want="$(awk -v f="$leaf" '{ n = $2; sub(/^\*/, "", n); if (n == f) { print tolower($1); exit } }' "$TMP/sums")"
    if [ -z "$want" ]; then rm -f "$2"; echo "$leaf is not listed in $(basename "$3"), so it was not kept (a new release may still be uploading - re-run shortly)" >&2; exit 1; fi
    if [ "$4" = "md5" ]; then got="$(md5sum "$2" | cut -d' ' -f1)"; else got="$(sha256sum "$2" | cut -d' ' -f1)"; fi
    if [ "$got" != "$want" ]; then rm -f "$2"; echo "$leaf does not match its published $4 checksum, so it was deleted and not used" >&2; exit 1; fi
    echo "[tools]   $4 checksum verified"
}

if [ -e "$TOOLS/yt-dlp" ] && [ "$FORCE" != "1" ]; then
    echo "[tools] yt-dlp already present - re-run with FORCE=1 to replace it"
elif [ "$RID" = "linux-arm" ]; then
    command -v unzip >/dev/null 2>&1 || { echo "need unzip for the armv7l build" >&2; exit 1; }
    fetch "$YT_URL" "$TMP/yt.zip" "$YT_SUMS" sha256
    ( cd "$TMP" && unzip -qo yt.zip -d yt )
    # An empty find would leave the script "succeeding" with no yt-dlp, so name the file before copying.
    ytbin="$(find "$TMP/yt" -type f -name 'yt-dlp*' -print -quit || true)"
    [ -n "$ytbin" ] || { echo "no yt-dlp binary inside $(basename "$YT_URL")" >&2; exit 1; }
    cp "$ytbin" "$TOOLS/yt-dlp"
else
    fetch "$YT_URL" "$TMP/yt-dlp" "$YT_SUMS" sha256
    cp "$TMP/yt-dlp" "$TOOLS/yt-dlp"
fi

if [ -e "$TOOLS/ffmpeg" ] && [ -e "$TOOLS/ffprobe" ] && [ "$FORCE" != "1" ]; then
    echo "[tools] ffmpeg and ffprobe already present - re-run with FORCE=1 to replace them"
else
    fetch "$FF_URL" "$TMP/ff.tar.xz" "$FF_SUMS" "$FF_ALGO"
    mkdir -p "$TMP/ff"
    echo "[tools] extracting ffmpeg"
    tar -xf "$TMP/ff.tar.xz" -C "$TMP/ff"
    for want in ffmpeg ffprobe; do
        f="$(find "$TMP/ff" -type f -name "$want" -print -quit || true)"
        if [ -n "$f" ]; then cp "$f" "$TOOLS/$want"
        elif [ "$want" = "ffmpeg" ]; then echo "no ffmpeg inside $(basename "$FF_URL")" >&2; exit 1
        # yt-dlp reads ffprobe out of --ffmpeg-location to merge video and audio, so a quiet miss fails at play time.
        else echo "[tools] WARNING: no ffprobe in the archive - video merging will fail without it." >&2; fi
    done
fi

chmod +x "$TOOLS"/* 2>/dev/null || true
echo "[tools] marked the binaries executable"
echo
echo "[tools] done - $TOOLS"
ls -lh "$TOOLS" | tail -n +2 | awk '{printf "        %-14s %8s\n", $9, $5}'
echo
echo "KMH finds this Tools folder on its own when it sits beside KMHServerAddon, or inside KMH-Data."
echo "Restart the server; the boot log should read 'Video server: using yt-dlp' and 'using ffmpeg'."
echo "Moving this to another machine? Take the whole Tools folder - both binaries have to travel."
