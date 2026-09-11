# Fetch yt-dlp and ffmpeg for the KMH video server, into a Tools folder KMH already searches.
#
#   .\get-media-tools.ps1                      # this machine's architecture, Tools\ beside the script
#   .\get-media-tools.ps1 -Rid linux-arm64     # fetch for another machine, then copy Tools\ across
#   .\get-media-tools.ps1 -Dest D:\server      # write into D:\server\Tools
#
# No net on the server? Run this on any machine that has one and copy the whole Tools folder to the
# server, beside KMHServerAddon.exe. Nothing here phones home at runtime.
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
$tools = Join-Path $Dest 'Tools'
New-Item -ItemType Directory -Path $tools -Force | Out-Null

# yt-dlp ships one file per architecture; ffmpeg comes as an archive we take two binaries out of.
$ytUrl = switch ($Rid) {
    'win-x64'     { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' }
    'win-x86'     { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_x86.exe' }
    'win-arm64'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_arm64.exe' }
    'linux-x64'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux' }
    'linux-arm64' { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_aarch64' }
    'linux-arm'   { 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux_armv7l.zip' }
}
# LGPL, not GPL: a GPL ffmpeg would put its own redistribution terms on whoever the server gets handed
# to next, and KMH only transcodes - nothing GPL-only ever gets used.
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

function Fetch([string]$url, [string]$outFile) {
    Write-Host "[tools] downloading $(Split-Path $url -Leaf)"
    Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -TimeoutSec 300
    $len = (Get-Item $outFile).Length
    if ($len -lt 100KB) { throw "$outFile came back only $len bytes - the download did not complete" }
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
        $z = Join-Path $tmp 'yt.zip'
        Fetch $ytUrl $z | Out-Null
        Expand-Archive -Path $z -DestinationPath (Join-Path $tmp 'yt') -Force
        $f = Get-ChildItem (Join-Path $tmp 'yt') -Recurse -File |
             Where-Object { $_.Name -eq 'yt-dlp' -or $_.Name -like 'yt-dlp_*' } | Select-Object -First 1
        if (-not $f) { throw "no yt-dlp binary inside $(Split-Path $ytUrl -Leaf)" }
        Copy-Item $f.FullName $ytOut -Force
    }
    else { Fetch $ytUrl $ytOut | Out-Null }

    $ffOut = Join-Path $tools $ffName
    if (-not $ffUrl) {
        Write-Host "[tools] NOTE: nobody publishes a current 32-bit Windows ffmpeg, so win-x86 gets yt-dlp only." -ForegroundColor Yellow
        Write-Host "[tools]       The video server needs both, so it stays off on that build."                    -ForegroundColor Yellow
    }
    elseif ((Test-Path $ffOut) -and -not $Force) {
        Write-Host "[tools] $ffName already present - re-run with -Force to replace it"
    }
    else {
        $archive = Join-Path $tmp (Split-Path $ffUrl -Leaf)
        Fetch $ffUrl $archive | Out-Null
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
