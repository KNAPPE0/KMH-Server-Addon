# Proves the contract check passes with -WithoutGlobal when the private KMH-Global is absent, and fails without the flag.

$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot
$client = Join-Path (Split-Path -Parent $repo) 'KMH-Patch'
$work   = Join-Path ([System.IO.Path]::GetTempPath()) ('kmh-contract-noglobal-' + [guid]::NewGuid().ToString('N'))

function RunCheck([string[]]$extra) {
    # A check that crashes writes to stderr, which is a result to report here, not a reason to stop.
    $ErrorActionPreference = 'Continue'
    $check = Join-Path $work 'KMH-Server-Addon\tools\protocol-contract-check.ps1'
    $out = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $check @extra 2>&1 | ForEach-Object { "$_" })
    [pscustomobject]@{
        Exit   = $LASTEXITCODE
        Holds  = @($out -match 'Contract holds').Count -gt 0
        Ended  = @($out -match '=== RESULT ===').Count -gt 0
        Notes  = @($out -match 'note: KMH-Global is not checked out').Count
        Global = @($out -match 'FAIL: KMH-Global is not beside this repo').Count
        Other  = @($out -match 'FAIL:' | Where-Object { $_ -notmatch 'KMH-Global is not beside this repo' })
        Error  = @($out -match 'not recognized|Exception' | Select-Object -First 1)
    }
}

try {
    # Both repos side by side with no KMH-Global, as in a public checkout.
    foreach ($r in @(@{ From = $repo; To = 'KMH-Server-Addon' }, @{ From = $client; To = 'KMH-Patch' })) {
        robocopy $r.From (Join-Path $work $r.To) /E /XD bin obj .git .vs payload /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "copying $($r.From) failed (robocopy exit $LASTEXITCODE)" }
    }
    $skip   = RunCheck @('-WithoutGlobal')
    $strict = RunCheck @()
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

$results = [ordered]@{
    'both runs reach RESULT'    = ($skip.Ended -and $strict.Ended)
    'skips and holds'           = ($skip.Exit -eq 0 -and $skip.Holds -and $skip.Notes -gt 0 -and $skip.Other.Count -eq 0)
    'strict fails on Global'    = ($strict.Exit -eq 1 -and $strict.Global -gt 0 -and $strict.Notes -eq 0)
    'strict fails nothing else' = ($strict.Other.Count -eq 0)
    'every skip is a failure'   = ($strict.Global -eq $skip.Notes)
}
$bad = 0
foreach ($k in $results.Keys) {
    if ($results[$k]) { Write-Host ("  OK    {0}" -f $k) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0}" -f $k) -ForegroundColor Red; $bad++ }
}
Write-Host ("  -WithoutGlobal: exit $($skip.Exit), $($skip.Notes) skipped; strict: exit $($strict.Exit), " +
            "$($strict.Global) Global failure(s), $($strict.Other.Count) other")
foreach ($o in @($skip.Other) + @($strict.Other) + @($skip.Error) + @($strict.Error)) { Write-Host "    $o" -ForegroundColor Red }

if ($bad -gt 0) { Write-Host '  the contract check does not run cleanly without KMH-Global.' -ForegroundColor Red; exit 1 }
Write-Host '  the contract check skips KMH-Global only when told to, and fails without it otherwise.' -ForegroundColor Green
exit 0
