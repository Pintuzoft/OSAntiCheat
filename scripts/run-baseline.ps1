<#
.SYNOPSIS
  Replays a demo archive through the detectors to build the population baseline.

.DESCRIPTION
  Reads .dem.gz straight from the archive (inflated in RAM), so nothing is copied to disk
  and the demos stay where they are. Results are appended per demo and finished demos are
  skipped, so this is safe to interrupt - rerun the same command and it resumes.

  Two outputs:
    -Out    one row per player-session: peak score, alive minutes, signal counts.
            aliveMinutes is the exposure; without it a rate means nothing.
    -Shots  one row per shot: aim error, and on target switches the degrees travelled
            and milliseconds taken, plus how long the enemy sat in the crosshair before
            the shot. This is the raw material for setting thresholds off the population
            instead of guessing them.

.EXAMPLE
  .\scripts\run-baseline.ps1 -DemoPath C:\Users\fredde\Documents\demo_scan -Jobs 20

.EXAMPLE
  .\scripts\run-baseline.ps1 -DemoPath \\nas\demos\workshop -Since 20260501 -Jobs 20

.NOTES
  Needs the .NET 10 SDK. Each parallel job holds one inflated demo in memory
  (~100-400 MB), so keep Jobs * 400 MB under your RAM.

  Keep this file pure ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI, and a
  UTF-8 em-dash decodes to a byte that PowerShell treats as a quote, which breaks parsing.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DemoPath,

    [int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount - 4),

    [string]$Out = "baseline.csv",

    [string]$Shots = "shots.csv",

    # Archive filenames start with the date, so a window costs nothing to select.
    # 20260501 onward covers all three reference players' recent sessions.
    [string]$Since = "",

    [string]$Until = "",

    # Sample N demos spread across the archive instead of replaying all of them. Use this to sanity
    # check a measurement before committing an hour: a broken metric is broken at 200 demos too.
    [int]$Limit = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path $DemoPath)) { throw "Demo path not found: $DemoPath" }

Write-Host "Demos : $DemoPath"
Write-Host "Jobs  : $Jobs   (~$([Math]::Round($Jobs * 0.4, 1)) GB RAM at peak)"
Write-Host "Out   : $Out"
Write-Host "Shots : $Shots"
Write-Host "Safe to Ctrl-C - rerun this exact command to resume."
Write-Host ""

Push-Location $root
try {
    $osacArgs = @($DemoPath, "--jobs", $Jobs, "--csv", $Out, "--shots", $Shots)
    if ($Since) { $osacArgs += @("--since", $Since) }
    if ($Until) { $osacArgs += @("--until", $Until) }
    if ($Limit -gt 0) { $osacArgs += @("--limit", $Limit) }

    dotnet run --project tools/DemoReplay -c Release -- @osacArgs
}
finally {
    Pop-Location
}
