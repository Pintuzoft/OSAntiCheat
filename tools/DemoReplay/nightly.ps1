<#
    nightly.ps1 — run the full offline detection suite on the last day's demos and report.

    The live plugin catches what it has ported (spinbot, bone-lock, anti-recoil); this catches
    EVERYTHING (deadaim, null-test too), on fresh — still playable — demos, so every flag points at
    a demo an admin can actually review. Zero risk: offline, it can only log, never ban.

    Schedule it (Task Scheduler, nightly) after the server has rotated the day's demos. Detections go
    to a dated log and, if a Discord webhook is set, to that channel — out-of-game, so a playing admin
    never sees a live tip (the same "silent while it's played" rule the plugin follows).

    Usage:
      .\nightly.ps1 -DemoDir \\colanas2\demos\demos -Days 1
      .\nightly.ps1 -DemoDir \\colanas2\demos\demos -Days 1 -Webhook https://discord.com/api/webhooks/...
#>
param(
    [Parameter(Mandatory = $true)] [string] $DemoDir,
    [int]    $Days       = 1,
    [string] $OutDir     = "$PSScriptRoot\..\..\private\nightly",
    [string] $SkipFile   = "$PSScriptRoot\..\..\fails2.txt",
    [string] $Webhook    = ""
)

$ErrorActionPreference = "Stop"
$exe   = Join-Path $PSScriptRoot "bin\Release\net10.0\osac-replay.exe"
$since = (Get-Date).AddDays(-$Days).ToString("yyyyMMdd")
$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$csv   = Join-Path $OutDir "$stamp.csv"
$kills = Join-Path $OutDir "$stamp-kills.csv"
$log   = Join-Path $OutDir "$stamp.log"

$args = @($DemoDir, "--since", $since, "--csv", $csv, "--kills", $kills)
if (Test-Path $SkipFile) { $args += @("--skip", $SkipFile) }

Write-Host "osac-replay --since $since (last $Days day(s)) ..."
& $exe @args 2>&1 | Tee-Object -FilePath $log

# Re-run the tuned detections report from the CSVs (thresholds without a re-parse).
$report = & python3 (Join-Path $PSScriptRoot "report.py") --players $csv --kills $kills 2>&1 | Out-String
$report | Tee-Object -FilePath (Join-Path $OutDir "$stamp-detections.txt")

# The detections block only — for the Discord post.
$detections = ($report -split "=== DETECTIONS")[1]
if ($null -ne $detections) { $detections = "=== DETECTIONS" + $detections }

if ($Webhook -and $detections -and $detections.Trim().Length -gt 0) {
    # Post out-of-game so a playing admin never gets a live tip. Truncate to Discord's 2000-char limit.
    $content = "``````" + ($detections.Substring(0, [Math]::Min(1900, $detections.Length))) + "``````"
    $body = @{ content = $content } | ConvertTo-Json
    try   { Invoke-RestMethod -Uri $Webhook -Method Post -ContentType 'application/json' -Body $body }
    catch { Write-Warning "Discord post failed: $_" }
}

Write-Host "`nDone. Detections: $OutDir\$stamp-detections.txt"
Write-Host "NOTE: CSVs carry player names/SteamIDs — keep them in private/, never commit."
