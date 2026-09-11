KMH media tools - yt-dlp + ffmpeg for the in-game video server
==============================================================

Players can watch a video link inside the game. Two programs do the actual work
and KMH does not ship them:

  yt-dlp   resolves the link
  ffmpeg   turns what comes back into something the game can play

Miss either one and the video server stays off. Nothing else changes - the rest
of KMH runs exactly as before, and the boot log tells you which one is missing.


Getting them
------------
Run the script for your platform:

  Windows   .\get-media-tools.ps1
  Linux     ./get-media-tools.sh

It downloads both into the Tools folder next to it and marks them executable.

These scripts also come with the server, already in the Tools folder beside
KMHServerAddon. Run them there and the binaries land where KMH looks. Run them
anywhere else and you will need to move the finished Tools folder next to
KMHServerAddon (or into KMH-Data) yourself.

Restart the server afterwards. No config edit either way. If you already have
copies you would rather use, put their paths in VideoHelperPath and FfmpegPath
in KMH-Data/Config/Media.json.


No internet on the server
-------------------------
Common enough on a locked-down host, and not a problem. Run the script on a
machine that does have internet, then move the Tools folder across however you
normally move files - SFTP, panel upload, a stick. Nothing reaches out again
once the binaries are in place.

Tell it which machine you are fetching for:

  .\get-media-tools.ps1 -Rid linux-arm64
  ./get-media-tools.sh linux-arm64


What it downloads
-----------------
  yt-dlp    github.com/yt-dlp/yt-dlp      - Unlicense (public domain)
  ffmpeg    github.com/BtbN/FFmpeg-Builds - LGPL build
            (32-bit ARM Linux comes from johnvansickle.com instead - that is
             where the current armhf static builds live)

The LGPL ffmpeg is a choice, not an accident. A GPL build would carry its own
redistribution terms to whoever you hand the server to next, and KMH only ever
transcodes - none of the GPL-only pieces get used.

Both are somebody else's programs under somebody else's licence. The scripts
fetch the official builds and change nothing about them.

Watch out for one thing: there is no current 32-bit Windows ffmpeg from anyone,
so on win-x86 you get yt-dlp and the video server stays off.


Did it work?
------------
Restart and look for these:

  Video server: using yt-dlp.exe
  Video server: using ffmpeg.exe
  Video server: sharing the KMH API port 5099 - no extra port to open.

That last line means there is nothing extra to open on the firewall. Set
VideoServerPort in Media.json if you would rather it had its own listener.

Still says "off - yt-dlp and ffmpeg not found"? Then the binaries are somewhere
KMH does not look. It checks these, beside KMHServerAddon and then again inside
KMH-Data:

  the folder itself
  Tools, plus any folder inside Tools, plus that folder's bin
  bin
  any <folder>/bin

So unzipping an ffmpeg archive straight into Tools is fine - you do not have to
dig the binary out first. Dropping it loose in Tools is simpler still.
