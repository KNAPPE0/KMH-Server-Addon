# KMH Server Addon - build one release asset.
#
# Usage:
#   .\deploy.ps1
#   .\deploy.ps1 -Rid linux-x64
#   .\deploy.ps1 -Rid linux-x64 -SelfContained
#   .\deploy.ps1 -Rid win-x64 -SkipPack
param(
    [string]$Rid = "win-x64",
    [switch]$SelfContained,
    [switch]$SkipPack,
    [string]$Deploy = (Join-Path $PSScriptRoot "Deploy")
)

$ErrorActionPreference = "Stop"

function Get-ProjectVersion {
    param([string]$ProjectPath)

    $match = Select-String -Path $ProjectPath -Pattern "<Version>([^<]+)</Version>" -List
    if ($match -and $match.Matches.Count -gt 0) {
        return $match.Matches[0].Groups[1].Value
    }

    return "0.0.0"
}

# Package guard: RWT and its native deps are referenced Private=False, so they must never appear in our output.
function Assert-NoUnexpectedBinaries {
    param([string]$Path)

    $blocked = @(
        "GameServer", "GameClient", "RTServer", "RTClient",
        "Shared", "RTShared", "TCPNetwork", "RTNetwork",
        "MessagePack", "Mono.Nat"
    )

    $found = Get-ChildItem $Path -Recurse -File -Include *.dll,*.exe -ErrorAction SilentlyContinue |
        Where-Object { $blocked -contains [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }

    if ($found) {
        throw "Unexpected binary in publish: $(($found | ForEach-Object Name) -join ', ')."
    }

    Write-Host "[deploy] Package guard OK - no unexpected binaries."
}

# A missing payload starts fine and fails only on that server generation, so check the shipped assembly itself
# rather than trusting the build inputs. The payloads live in KMHServerAddon.dll - the .exe is only a launcher stub.
function Assert-PayloadsEmbedded {
    param([string]$AssemblyPath)

    $bytes = [System.IO.File]::ReadAllBytes($AssemblyPath)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    foreach ($name in @("KMHAddon.RTShared.dll", "KMHAddon.RTServer.dll")) {
        if ($text.IndexOf($name) -lt 0) {
            throw "Published executable does not embed $name - that RWT generation would be unsupported."
        }
    }

    Write-Host "[deploy] Payload embedding OK - both new-generation payloads are inside $([System.IO.Path]::GetFileName($AssemblyPath))."
}

# Nothing is bundled into the exe any more, so Newtonsoft has to ship as a loose file or the addon dies on first load.
function Assert-NewtonsoftPresent {
    param([string]$Path, [string]$BundleExe)

    # Single-file builds bundle it into the exe, so there is no loose DLL to find - look inside the bundle instead.
    if ($BundleExe) {
        $bytes = [System.IO.File]::ReadAllBytes($BundleExe)
        if ([System.Text.Encoding]::UTF8.GetString($bytes).IndexOf("Newtonsoft.Json.dll") -lt 0) {
            throw "Single-file executable does not bundle Newtonsoft.Json.dll."
        }
        Write-Host "[deploy] Dependency OK - Newtonsoft.Json.dll bundled into the executable."
        return
    }

    if (-not (Test-Path (Join-Path $Path "Newtonsoft.Json.dll"))) {
        throw "Package is missing Newtonsoft.Json.dll."
    }

    Write-Host "[deploy] Dependency OK - Newtonsoft.Json.dll included."
}

$project = Join-Path $PSScriptRoot "Source\KMHServerAddon.csproj"
$sdkProject = Join-Path $PSScriptRoot "Source\KMH.Sdk.Server\KMH.Sdk.Server.csproj"
$releases = Join-Path $PSScriptRoot "Releases"

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

if (-not (Test-Path $releases)) {
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
}

$version = Get-ProjectVersion -ProjectPath $project
$ext = if ($Rid.StartsWith("win")) { ".exe" } else { "" }
$publishName = if ($SelfContained) { "$Rid-selfcontained" } else { $Rid }
$publish = Join-Path $PSScriptRoot "Source\bin\publish\$publishName"

# One payload per new-generation RWT build; a stale one silently ships old code to that generation.
# Rebuild first, then verify each version.
$payloads = @(
    @{ Flavor = "New"; Dll = "KMHAddon.RTShared.dll"; For = "26.6.x (GameServer.dll)" },
    @{ Flavor = "RT";  Dll = "KMHAddon.RTServer.dll"; For = "renamed build (RTServer.dll)" }
)

foreach ($p in $payloads) {
    Write-Host "[deploy] Building the $($p.Flavor) payload for $($p.For) so the launcher embeds a fresh one."
    & dotnet build $project -c Release -p:RwtFlavor=$($p.Flavor) -v q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Payload build (RwtFlavor=$($p.Flavor)) failed with exit code $LASTEXITCODE."
    }

    $payloadDll = Join-Path $PSScriptRoot "Source\bin\payload\$($p.Dll)"
    if (-not (Test-Path $payloadDll)) {
        throw "Payload missing after build: $payloadDll"
    }

    $payloadVersion = [System.Reflection.AssemblyName]::GetAssemblyName($payloadDll).Version.ToString(3)
    if ($payloadVersion -ne $version) {
        throw "Payload version $payloadVersion does not match project version $version - stale payload would ship."
    }

    # The version check only catches a payload from a different release. MSBuild skips a rebuild when a source file's
    # timestamp is older than the output - which is exactly what a restored file looks like - so "Build succeeded"
    # can compile nothing and leave the previous code in the payload. Compare timestamps, not just versions.
    $newestSource = Get-ChildItem (Join-Path $PSScriptRoot "Source") -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newestSource -and $newestSource.LastWriteTime -gt (Get-Item $payloadDll).LastWriteTime) {
        throw "Payload $($p.Dll) is older than $($newestSource.Name) - the build was skipped and this payload holds stale code. Touch the source and rebuild."
    }

    Write-Host "[deploy] Payload OK - $([System.IO.Path]::GetFileNameWithoutExtension($p.Dll)) v$payloadVersion matches project v$version."
}

if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

if ($SelfContained) {
    Write-Host "[deploy] Publishing KMHServerAddon v$version for $Rid, self-contained folder, no RWT bundled."

    & dotnet publish $project `
        -c Release `
        -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:CopyLocalLockFileAssemblies=true `
        -p:DebugType=none `
        -o $publish | Out-Host
}
else {
    Write-Host "[deploy] Publishing KMHServerAddon v$version for $Rid, framework-dependent, no RWT bundled."

    # Single-file is the shipped layout for framework-dependent builds: one KMHServerAddon.exe beside KMH-Data,
    # README and SETUP, so an owner drops one file next to their RWT server. Self-contained builds stay multi-file
    # (see the branch above) - bundling a whole runtime into one binary is what trips antivirus heuristics.
    & dotnet publish $project `
        -c Release `
        -r $Rid `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:CopyLocalLockFileAssemblies=true `
        -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=none `
        -o $publish | Out-Host
}

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed for $Rid$(if ($SelfContained) { ' self-contained' } else { '' }) with exit code $LASTEXITCODE."
}

$exe = Join-Path $publish "KMHServerAddon$ext"
if (-not (Test-Path $exe)) {
    throw "Published executable not found: $exe"
}

Assert-NoUnexpectedBinaries -Path $publish

# Framework-dependent publishes are single-file, so KMHServerAddon.dll only exists inside the bundle - check the exe
# itself. Self-contained publishes keep the loose layout and are checked the original way.
$singleFile = -not $SelfContained
if ($singleFile) {
    Assert-PayloadsEmbedded -AssemblyPath $exe
    Assert-NewtonsoftPresent -BundleExe $exe

    # The zip deliberately ships no loose KMH.Sdk.Server.dll, so the executable must carry it or an extension that
    # references the SDK fails to resolve it at load time.
    $exeBytes = [System.IO.File]::ReadAllBytes($exe)
    if ([System.Text.Encoding]::UTF8.GetString($exeBytes).IndexOf("KMH.Sdk.Server.dll") -lt 0) {
        throw "Single-file executable does not bundle KMH.Sdk.Server.dll - extensions would fail to load."
    }
    Write-Host "[deploy] SDK bundling OK - KMH.Sdk.Server.dll is inside the executable."
}
else {
    Assert-PayloadsEmbedded -AssemblyPath (Join-Path $publish "KMHServerAddon.dll")
    Assert-NewtonsoftPresent -Path $publish
}

if (Test-Path $Deploy) {
    Remove-Item (Join-Path $Deploy "*") -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $Deploy -Force | Out-Null
}

Copy-Item (Join-Path $publish "*") $Deploy -Recurse -Force

$template = Join-Path $PSScriptRoot "Templates\kmh-data"
if (Test-Path $template) {
    $dataOut = Join-Path $Deploy "KMH-Data"
    if (Test-Path $dataOut) {
        Remove-Item $dataOut -Recurse -Force
    }
    Copy-Item $template $dataOut -Recurse -Force
}

# The fetch scripts ship in Tools/, which is both where they download to and somewhere KMH already looks,
# so wanting in-game video does not mean hunting down a second download.
$mediaTpl = Join-Path $PSScriptRoot "Templates\media-tools"
if (Test-Path $mediaTpl) {
    $toolsOut = Join-Path $Deploy "Tools"
    New-Item -ItemType Directory -Path $toolsOut -Force | Out-Null
    # Named files only, never a wildcard: the fetch script defaults to downloading beside itself, so running it
    # in place would leave ~270 MB of ffmpeg in this folder and a wildcard would ship that in all twelve zips.
    foreach ($f in @("get-media-tools.ps1", "get-media-tools.sh", "README.txt")) {
        $src = Join-Path $mediaTpl $f
        if (-not (Test-Path $src)) { throw "Templates\media-tools\$f is missing - the video tools would ship unusable." }
        Copy-Item $src (Join-Path $toolsOut $f) -Force
    }
    Write-Host "[deploy] Media tools staged into Tools/ (get-media-tools + README)."
}

# Discord embed icons ship in every zip at KMH-Data/Icons (the one folder the addon reads). Staged from the
# committed source set and guarded so they can never silently drop out of a release again.
$iconsSrc = Join-Path $PSScriptRoot "Source\Assets\Icons"
$iconsOut = Join-Path $Deploy "KMH-Data\Icons"
New-Item -ItemType Directory -Path $iconsOut -Force | Out-Null
Copy-Item (Join-Path $iconsSrc "*") $iconsOut -Recurse -Force
$iconCount = (Get-ChildItem $iconsOut -Filter *.png -ErrorAction SilentlyContinue | Measure-Object).Count
if ($iconCount -lt 1) {
    throw "No icons staged into KMH-Data/Icons - Source\Assets\Icons is missing or empty."
}
Write-Host "[deploy] Icons OK - $iconCount icon(s) staged into KMH-Data/Icons."

$setup = Join-Path $PSScriptRoot "SETUP.txt"
if (Test-Path $setup) {
    Copy-Item $setup $Deploy -Force
}

$readme = Join-Path $PSScriptRoot "README.md"
if (Test-Path $readme) {
    Copy-Item $readme $Deploy -Force
}

# The SDK must land in Deploy/ - the csproj says it ships there and Templates\ServerExtension references
# ..\..\Deploy\KMH.Sdk.Server.dll, so without this the flagship example doesn't compile for an extension author.
# (Self-contained builds got it incidentally by copying the whole publish folder; framework-dependent didn't.)
$sdkBuilt = Join-Path $PSScriptRoot "Source\KMH.Sdk.Server\bin\Release\net8.0\KMH.Sdk.Server.dll"
if (-not (Test-Path $sdkBuilt)) {
    & dotnet build $sdkProject -c Release -v q | Out-Host
}
if (Test-Path $sdkBuilt) {
    Copy-Item $sdkBuilt $Deploy -Force
    $sdkDocs = [System.IO.Path]::ChangeExtension($sdkBuilt, ".xml")   # IntelliSense for extension authors
    if (Test-Path $sdkDocs) { Copy-Item $sdkDocs $Deploy -Force }
    Write-Host "[deploy] SDK OK - KMH.Sdk.Server.dll staged into Deploy/ (the path extension templates reference)."
}
else {
    throw "KMH.Sdk.Server.dll not found after build - extension templates reference Deploy\KMH.Sdk.Server.dll."
}

if (-not $SkipPack -and (Test-Path $sdkProject)) {
    Write-Host "[deploy] Packing KMH.Sdk.Server NuGet package."
    & dotnet pack $sdkProject -c Release -o $releases | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SDK pack failed with exit code $LASTEXITCODE."
    }
}

$assetName = "KMHServerAddon-v$version-$Rid$(if ($SelfContained) { '-selfcontained' } else { '' }).zip"
$zip = Join-Path $releases $assetName
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

# Written entry-by-entry, NOT with Compress-Archive or CreateFromDirectory. On Windows PowerShell 5.1 a wildcard
# Compress-Archive flattens subdirectories (KMH-Data/Icons ended up at the archive root, and the two README.txt
# files collided there), and CreateFromDirectory writes '\' separators, which are not valid ZIP entry names and
# unpack as one literal filename on Linux. Relative paths with '/' are the portable form.
Add-Type -AssemblyName System.IO.Compression            # ZipArchive / ZipArchiveMode
# Single-file carries the SDK inside the exe, so a loose copy would only shadow it; a self-contained publish
# resolves it as an ordinary loose dependency, and excluding it there ships a zip that cannot start.
$zipExclude = if ($singleFile) { @("KMH.Sdk.Server.dll", "KMH.Sdk.Server.xml") } else { @() }

Add-Type -AssemblyName System.IO.Compression.FileSystem # ZipFile / ZipFileExtensions
$zipRoot   = (Resolve-Path $Deploy).Path.TrimEnd('\') + '\'
$zipWriter = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in Get-ChildItem $Deploy -Recurse -File) {
        $rel = $item.FullName.Substring($zipRoot.Length).Replace('\', '/')
        if ($zipExclude -contains $rel) { continue }   # top-level only; a same-named file under KMH-Data still ships
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipWriter, $item.FullName, $rel)
    }
}
finally { $zipWriter.Dispose() }

# The staged-folder icon check above passes even when the archive is wrong, so verify the shipped zip itself.
$zipCheck = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $zipIcons = @($zipCheck.Entries | Where-Object { $_.FullName -like "KMH-Data/Icons/*.png" }).Count
    if ($zipIcons -lt 1) {
        throw "Release zip has no KMH-Data/Icons/*.png - the archive layout is flattened and the addon will not find its icons."
    }
    Write-Host "[deploy] Zip layout OK - $zipIcons icon(s) at KMH-Data/Icons inside the archive."

    # Opposite requirement per layout - checking only the single-file one shipped a dead self-contained zip.
    if ($singleFile) {
        $straySdk = @($zipCheck.Entries | Where-Object { $zipExclude -contains $_.FullName }).Count
        if ($straySdk -gt 0) {
            throw "Release zip ships a loose KMH.Sdk.Server file - owners do not need it and it can shadow the bundled copy."
        }
        Write-Host "[deploy] Zip contents OK - no loose SDK beside the executable."
    }
    else {
        if (-not @($zipCheck.Entries | Where-Object { $_.FullName -eq "KMH.Sdk.Server.dll" }).Count) {
            throw "Self-contained release zip has no KMH.Sdk.Server.dll - KMHServerAddon.dll references it as a loose assembly, so the server cannot start."
        }
        Write-Host "[deploy] Zip contents OK - KMH.Sdk.Server.dll ships beside the multi-file build."
    }
}
finally { $zipCheck.Dispose() }

$zipMb = [Math]::Round((Get-Item $zip).Length / 1MB, 2)
$exeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 2)

Write-Host ""
Write-Host "[deploy] Done." -ForegroundColor Green
Write-Host "[deploy] Release asset: $zip ($zipMb MB)"

# This builds ONE target into the shared Releases folder, so the others are now whatever a previous run left there.
$siblings = @(Get-ChildItem (Split-Path $zip -Parent) -Filter "KMHServerAddon-v*.zip" -File -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -ne $zip -and $_.LastWriteTime -lt (Get-Item $zip).LastWriteTime })
if ($siblings.Count -gt 0) {
    Write-Host "[deploy] NOTE: $($siblings.Count) other release zip(s) in this folder are older than the one just built." -ForegroundColor Yellow
    Write-Host "[deploy]       Run release-all.ps1 before publishing - it rebuilds every target and verifies contents." -ForegroundColor Yellow
}
Write-Host "[deploy] App:           $exe ($exeMb MB)"

$rwtExe = "GameServer$ext / RTServer$ext"

Write-Host "[deploy] Install: extract the full zip beside the official RWT server ($rwtExe) and run KMHServerAddon$ext."
if ($SelfContained) {
    Write-Host "[deploy]          No .NET needed. Keep the files together - KMHServerAddon$ext is only a launcher stub."
} else {
    Write-Host "[deploy]          Requires .NET 8. Everything else is inside KMHServerAddon$ext."
}