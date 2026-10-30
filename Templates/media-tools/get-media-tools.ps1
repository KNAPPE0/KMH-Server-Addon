# Fetches yt-dlp, ffmpeg and ffprobe for the KMH video server into a Tools folder KMH already searches.
# Usage: .\get-media-tools.ps1 [-Rid linux-arm64] [-Dest D:\server] [-Force]   (default: this machine, Tools\ here)
# No net on the server? Run it elsewhere and copy the whole Tools folder beside KMHServerAddon.exe.
param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-arm')]
    [string]$Rid,
    [string]$Dest = $PSScriptRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest crawl

if (-not $Rid) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $win  = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [System.Runtime.InteropServices.OSPlatform]::Windows)
    $Rid = switch ("$(if ($win) { 'win' } else { 'linux' })-$arch") {
        'win-x64'     { 'win-x64' }   'win-x86'   { 'win-x86' }   'win-arm64' { 'win-arm64' }
        'linux-x64'   { 'linux-x64' } 'linux-arm64' { 'linux-arm64' } 'linux-arm' { 'linux-arm' }
        default       { if ($win) { 'win-x64' } else { 'linux-x64' } }
    }
    Write-Host "[tools] detected $Rid"
}

$isWin = $Rid -like 'win-*'
# The release ships this script inside Tools/, so the default -Dest must not get a second nested Tools folder.
$tools = if ((Split-Path $Dest -Leaf) -eq 'Tools') { $Dest } else { Join-Path $Dest 'Tools' }
New-Item -ItemType Directory -Path $tools -Force | Out-Null

$ytUrl = switch ($Rid) {
    'win-x64'     { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' }
    'win-x86'     { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_x86.exe' }
    'win-arm64'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_arm64.exe' }
    'linux-x64'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux' }
    'linux-arm64' { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64' }
    'linux-arm'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_armv7l.zip' }
}
# LGPL, not GPL, where one exists: GPL terms bind whoever the server is handed to next. Nobody publishes an LGPL
# 32-bit ARM build, so linux-arm gets johnvansickle's GPL one.
$ffUrl = switch ($Rid) {
    'win-x64'     { 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip' }
    'win-arm64'   { 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-lgpl.zip' }
    'linux-x64'   { 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-lgpl.tar.xz' }
    'linux-arm64' { 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-lgpl.tar.xz' }
    'linux-arm'   { 'https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-armhf-static.tar.xz' }
    'win-x86'     { $null }
}

$ytName = if ($isWin) { 'yt-dlp.exe' } else { 'yt-dlp' }
$ffName = if ($isWin) { 'ffmpeg.exe' } else { 'ffmpeg' }
$fpName = if ($isWin) { 'ffprobe.exe' } else { 'ffprobe' }

$ytSums = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS'
$ffSums = if ($Rid -eq 'linux-arm') { "$ffUrl.md5" } else { 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256' }
$ffAlgo = if ($Rid -eq 'linux-arm') { 'MD5' } else { 'SHA256' }

# The server runs these binaries, so each is checked against its publisher's checksum list from the same release.
function Fetch([string]$url, [string]$outFile, [string]$sumsUrl, [string]$algo) {
    $leaf = Split-Path $url -Leaf
    Write-Host "[tools] downloading $leaf"
    Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -TimeoutSec 300
    $len = (Get-Item $outFile).Length
    if ($len -lt 100KB) { throw "$outFile came back only $len bytes - the download did not complete" }
    $sums = (Invoke-WebRequest -Uri $sumsUrl -UseBasicParsing -TimeoutSec 60).Content
    if ($sums -is [byte[]]) { $sums = [System.Text.Encoding]::UTF8.GetString($sums) }
    $want = $null
    foreach ($line in ($sums -split "`n")) {
        $p = $line.Trim() -split '\s+', 2
        if ($p.Count -eq 2 -and $p[1].TrimStart('*') -eq $leaf) { $want = $p[0].ToLowerInvariant(); break }
    }
    if (-not $want) {
        Remove-Item $outFile -Force
        throw "$leaf is not listed in $(Split-Path $sumsUrl -Leaf), so it was not kept (a new release may still be uploading - re-run in a few minutes)"
    }
    if ((Get-FileHash -LiteralPath $outFile -Algorithm $algo).Hash.ToLowerInvariant() -ne $want) {
        Remove-Item $outFile -Force
        throw "$leaf does not match its published $algo checksum, so it was deleted and not used"
    }
    Write-Host "[tools]   $algo checksum verified"
    return $len
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("kmh-tools-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    $ytOut = Join-Path $tools $ytName
    if ((Test-Path $ytOut) -and -not $Force) {
        Write-Host "[tools] $ytName already present - re-run with -Force to replace it"
    }
    elseif ($Rid -eq 'linux-arm') {
        $z = Join-Path $tmp (Split-Path $ytUrl -Leaf)
        Fetch $ytUrl $z $ytSums 'SHA256' | Out-Null
        Expand-Archive -Path $z -DestinationPath (Join-Path $tmp 'yt') -Force
        $f = Get-ChildItem (Join-Path $tmp 'yt') -Recurse -File |
             Where-Object { $_.Name -eq 'yt-dlp' -or $_.Name -like 'yt-dlp_*' } | Select-Object -First 1
        if (-not $f) { throw "no yt-dlp binary inside $(Split-Path $ytUrl -Leaf)" }
        Copy-Item $f.FullName $ytOut -Force
    }
    else {
        $ytTmp = Join-Path $tmp (Split-Path $ytUrl -Leaf)
        Fetch $ytUrl $ytTmp $ytSums 'SHA256' | Out-Null
        Copy-Item $ytTmp $ytOut -Force
    }

    $ffOut = Join-Path $tools $ffName
    if (-not $ffUrl) {
        Write-Host "[tools] NOTE: nobody publishes a current 32-bit Windows ffmpeg, so win-x86 gets yt-dlp only." -ForegroundColor Yellow
        Write-Host "[tools]       The video server needs both, so it stays off on that build."                    -ForegroundColor Yellow
    }
    elseif ((Test-Path $ffOut) -and (Test-Path (Join-Path $tools $fpName)) -and -not $Force) {
        Write-Host "[tools] $ffName and $fpName already present - re-run with -Force to replace them"
    }
    else {
        $archive = Join-Path $tmp (Split-Path $ffUrl -Leaf)
        Fetch $ffUrl $archive $ffSums $ffAlgo | Out-Null
        $ex = Join-Path $tmp 'ff'
        New-Item -ItemType Directory -Path $ex -Force | Out-Null
        Write-Host "[tools] extracting ffmpeg"
        if ($archive -like '*.zip') { Expand-Archive -Path $archive -DestinationPath $ex -Force }
        else {
            # tar handles .tar.xz on Windows 10+ and every Linux that ships xz.
            & tar -xf $archive -C $ex
            if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $archive - install xz-utils and retry" }
        }
        foreach ($want in @($ffName, $fpName)) {
            $f = Get-ChildItem $ex -Recurse -File | Where-Object { $_.Name -eq $want } | Select-Object -First 1
            if ($f) { Copy-Item $f.FullName (Join-Path $tools $want) -Force }
            elseif ($want -eq $ffName) { throw "no $want inside $(Split-Path $ffUrl -Leaf)" }
            # yt-dlp needs ffprobe in --ffmpeg-location to merge video and audio; a quiet miss fails at play time.
            else { Write-Host "[tools] WARNING: no $want in the archive - video merging will fail without it." -ForegroundColor Yellow }
        }
    }

    if (-not $isWin) {
        foreach ($f in Get-ChildItem $tools -File) {
            try { & chmod +x $f.FullName } catch { }
        }
        Write-Host "[tools] marked the binaries executable"
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "[tools] done - $tools" -ForegroundColor Green
Get-ChildItem $tools -File | ForEach-Object { "        {0,-14} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host ""
Write-Host "KMH finds this Tools folder on its own when it sits beside KMHServerAddon, or inside KMH-Data."
Write-Host "Restart the server; the boot log should read 'Video server: using yt-dlp.exe' and 'using ffmpeg.exe'."
Write-Host "Moving this to another machine? Take the whole Tools folder - both binaries have to travel."
