# Proves contract section 77 passes Discord-secret wording in comments and fails real secrets in code.

$ErrorActionPreference = 'Stop'
$repo    = Split-Path -Parent $PSScriptRoot
$client  = Join-Path (Split-Path -Parent $repo) 'KMH-Patch'
$check   = Join-Path $PSScriptRoot 'protocol-contract-check.ps1'
$probe   = Join-Path $client 'Source\Features\Discord\_S77Probe.cs'

function Run77 {
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $check 2>&1 | Out-String
    $sec = ($out -split '77\. The client never carries a Discord secret')[1]
    if ($null -eq $sec) { return 'SECTION-MISSING' }
    if ($sec -match 'client source looks like it carries a Discord credential') { return 'FAIL' }
    if ($sec -match 'no Discord bot token or client secret anywhere in the client') { return 'PASS' }
    return 'UNKNOWN'
}

$results = @{}

try {
    @'
namespace KMHPatch.Features.Discord;

// The client secret lives only on KMH Global. clientSecret = "never here".
// A Discord bot token must never be embedded: discordBotToken = "not this either".
// Authorization: "Bot AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKKLLLL" is a server-side header.
/* Block comment form of the same warning:
   client_secret = "still just prose"
   discordToken = "and so is this" */
/// <summary>Never store a clientSecret = "..." in the client.</summary>
internal static class S77Probe
{
    public const string ApplicationId = "1459619253010563327";
}
'@ | Set-Content -Path $probe -Encoding UTF8
    $results['comments only'] = Run77

    @'
namespace KMHPatch.Features.Discord;

internal static class S77Probe
{
    public const string ApplicationId = "1459619253010563327";
    private const string clientSecret = "not-a-real-secret-0000000000000000";
}
'@ | Set-Content -Path $probe -Encoding UTF8
    $results['real secret field'] = Run77

    @'
namespace KMHPatch.Features.Discord;

internal static class S77Probe
{
    public const string ApplicationId = "1459619253010563327";
    static string discordBotToken = "not.a-real-token.0000000000000000000000000";
}
'@ | Set-Content -Path $probe -Encoding UTF8
    $results['real bot token'] = Run77
}
finally {
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
}

$results['real client tree'] = Run77

$expected = @{
    'comments only'     = 'PASS'
    'real secret field' = 'FAIL'
    'real bot token'    = 'FAIL'
    'real client tree'  = 'PASS'
}

$bad = 0
foreach ($k in $expected.Keys | Sort-Object) {
    $got = $results[$k]; $want = $expected[$k]
    if ($got -eq $want) { Write-Host ("  OK    {0,-20} -> {1}" -f $k, $got) -ForegroundColor Green }
    else { Write-Host ("  WRONG {0,-20} -> {1} (expected {2})" -f $k, $got, $want) -ForegroundColor Red; $bad++ }
}

if ($bad -gt 0) { Write-Host "  section 77 cannot tell code from prose." -ForegroundColor Red; exit 1 }
Write-Host "  section 77 passes prose and fails code." -ForegroundColor Green
exit 0
