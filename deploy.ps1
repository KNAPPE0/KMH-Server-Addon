# Builds one KMH Server Addon release zip into Releases\ (plus the KMH.Sdk.Server NuGet package unless -SkipPack).
# Usage: .\deploy.ps1 [-Rid win-x64|linux-x64|...] [-SelfContained] [-SkipPack] [-Deploy <staging folder>]
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

# RWT and its native deps are referenced Private=False, so they must never appear in the output.
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

# A missing payload boots fine and fails only on that RWT generation, so check the shipped assembly, not the inputs.
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

# Without Newtonsoft.Json the addon dies on first load.
function Assert-NewtonsoftPresent {
    param([string]$Path, [string]$BundleExe)

    # Single-file builds bundle it, so there is no loose DLL to find.
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

# The Discord leaderboard image needs these; a server missing one boots and fails only when it draws.
function Assert-ImageDepsPresent {
    param([string]$Path, [string]$BundleExe)

    $needed = @("SixLabors.ImageSharp.dll", "SixLabors.Fonts.dll", "SixLabors.ImageSharp.Drawing.dll", "SixLabors.PolygonClipper.dll", "System.IO.Hashing.dll")
    if ($BundleExe) {
        $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($BundleExe))
        $missing = @($needed | Where-Object { $text.IndexOf($_) -lt 0 })
        if ($missing.Count -gt 0) { throw "Single-file executable does not bundle $($missing -join ', ') - the leaderboard image could not be drawn." }
        Write-Host "[deploy] Dependency OK - $($needed -join ', ') bundled into the executable."
        return
    }
    $missing = @($needed | Where-Object { -not (Test-Path (Join-Path $Path $_)) })
    if ($missing.Count -gt 0) { throw "Package is missing $($missing -join ', ') - the leaderboard image could not be drawn." }
    Write-Host "[deploy] Dependency OK - $($needed -join ', ') included."
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

    # Unset parts count as 0, so 1.3.1 matches its 1.3.1.0 assembly while 1.3.1.1 stays exact.
    $payloadVersion = [System.Reflection.AssemblyName]::GetAssemblyName($payloadDll).Version
    $w = [version]$version
    if ($payloadVersion -ne [version]::new($w.Major, $w.Minor, [Math]::Max($w.Build, 0), [Math]::Max($w.Revision, 0))) {
        throw "Payload version $payloadVersion does not match project version $version - stale payload would ship."
    }

    # MSBuild skips a rebuild when a restored source is older than the output, so "Build succeeded" can leave stale code.
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

    # Single-file only here: bundling a whole runtime (self-contained) into one binary trips antivirus heuristics.
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

# Framework-dependent publishes are single-file, so KMHServerAddon.dll exists only inside the exe.
$singleFile = -not $SelfContained
if ($singleFile) {
    Assert-PayloadsEmbedded -AssemblyPath $exe
    Assert-NewtonsoftPresent -BundleExe $exe
    Assert-ImageDepsPresent -BundleExe $exe

    # The zip ships no loose KMH.Sdk.Server.dll, so without it in the exe SDK-referencing extensions fail to load.
    $exeBytes = [System.IO.File]::ReadAllBytes($exe)
    if ([System.Text.Encoding]::UTF8.GetString($exeBytes).IndexOf("KMH.Sdk.Server.dll") -lt 0) {
        throw "Single-file executable does not bundle KMH.Sdk.Server.dll - extensions would fail to load."
    }
    Write-Host "[deploy] SDK bundling OK - KMH.Sdk.Server.dll is inside the executable."
}
else {
    Assert-PayloadsEmbedded -AssemblyPath (Join-Path $publish "KMHServerAddon.dll")
    Assert-NewtonsoftPresent -Path $publish
    Assert-ImageDepsPresent -Path $publish
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

# The fetch scripts ship in Tools/, which is where they download to and where KMH already looks.
$mediaTpl = Join-Path $PSScriptRoot "Templates\media-tools"
if (Test-Path $mediaTpl) {
    $toolsOut = Join-Path $Deploy "Tools"
    New-Item -ItemType Directory -Path $toolsOut -Force | Out-Null
    # Named files only: a run in place leaves ~270 MB of ffmpeg here, and a wildcard would ship it in every zip.
    foreach ($f in @("get-media-tools.ps1", "get-media-tools.sh", "README.txt")) {
        $src = Join-Path $mediaTpl $f
        if (-not (Test-Path $src)) { throw "Templates\media-tools\$f is missing - the video tools would ship unusable." }
        Copy-Item $src (Join-Path $toolsOut $f) -Force
    }
    Write-Host "[deploy] Media tools staged into Tools/ (get-media-tools + README)."
}

# KMH-Data/Icons is the only folder the addon reads Discord icons from; guarded so they cannot silently drop out.
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

# Bundled libraries and the embedded Inter fonts carry their own licences, which travel with every zip.
$notices = Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt"
if (-not (Test-Path $notices)) { throw "THIRD-PARTY-NOTICES.txt is missing - the zip would ship the Inter fonts without their licence." }
Copy-Item $notices $Deploy -Force
$license = Join-Path $PSScriptRoot "LICENSE"
if (-not (Test-Path $license)) { throw "LICENSE is missing - THIRD-PARTY-NOTICES.txt and README point at it." }
Copy-Item $license $Deploy -Force

# A self-contained zip carries the .NET runtime, so the runtime's own licence and notices travel with it.
if ($SelfContained) {
    $rc = Get-Content (Join-Path $Deploy "KMHServerAddon.runtimeconfig.json") -Raw | ConvertFrom-Json
    $rtVersion = @($rc.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq "Microsoft.NETCore.App" })[0].version
    $globalPackages = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
    $pack = Join-Path $globalPackages ("microsoft.netcore.app.runtime.$Rid".ToLowerInvariant() + "\$rtVersion")
    foreach ($n in "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT") {
        $src = Join-Path $pack $n
        if (-not (Test-Path $src)) { throw "The .NET runtime's $n was not found in $pack - a self-contained zip must carry it." }
        Copy-Item $src (Join-Path $Deploy "DOTNET-RUNTIME-$n") -Force
    }
    Write-Host "[deploy] .NET $rtVersion runtime licence and notices staged."
}

# Templates\ServerExtension references ..\..\Deploy\KMH.Sdk.Server.dll; without this copy it does not compile.
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

$assetName = "KMH-ColonyNexus-ServerAddon-v$version-$Rid$(if ($SelfContained) { '-selfcontained' } else { '' }).zip"
$zip = Join-Path $releases $assetName
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

# Entry by entry: PS 5.1 Compress-Archive with a wildcard flattens subfolders, and CreateFromDirectory writes '\'
# separators that unpack as one literal filename on Linux.
Add-Type -AssemblyName System.IO.Compression            # ZipArchive / ZipArchiveMode
# Single-file carries the SDK in the exe (a loose copy would shadow it); self-contained needs it loose to start.
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

    # Opposite requirement per layout: single-file must not ship the SDK loose, self-contained must.
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
$siblings = @(Get-ChildItem (Split-Path $zip -Parent) -Filter "KMH-ColonyNexus-ServerAddon-v*.zip" -File -ErrorAction SilentlyContinue |
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
    Write-Host "[deploy]          Requires .NET 10. Everything else is inside KMHServerAddon$ext."
}