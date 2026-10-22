# One-shot regression gate: build -> unit tests -> golden smoke.
#
# Why: the project's core discipline is "every change must show zero regression on the
# golden sets". That discipline was manual. This script makes it one command, so it can
# be wired into CI later (see docs/QUALITY_DEBT.md, 2026-10-22 engineering batch).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/verify.ps1
#   powershell -ExecutionPolicy Bypass -File tools/verify.ps1 -SkipGolden
#   powershell -ExecutionPolicy Bypass -File tools/verify.ps1 -Samples data/holdout

param(
    [switch]$SkipGolden,
    [string]$Samples = 'data/samples'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step([string]$name) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
}

Step 'build (Release)'
dotnet build DeepPCBStation.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED' -ForegroundColor Red; exit 1 }

Step 'unit tests'
dotnet test src/DeepPCBStation.Core.Tests/DeepPCBStation.Core.Tests.csproj -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { Write-Host 'TESTS FAILED' -ForegroundColor Red; exit 1 }

if ($SkipGolden) {
    Write-Host ""
    Write-Host 'Golden smoke skipped (-SkipGolden).' -ForegroundColor Yellow
    exit 0
}

Step "golden smoke ($Samples)"
if (-not (Test-Path $Samples)) {
    Write-Host "samples dir not found: $Samples" -ForegroundColor Red
    exit 1
}

dotnet run --project src/DeepPCBStation.ConsoleDemo -c Release --no-build -- --golden-eval --samples=$Samples | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'GOLDEN EVAL FAILED' -ForegroundColor Red; exit 1 }

$jsonPath = Join-Path $root 'reports/golden_latest.json'
$mdPath = Join-Path $root 'reports/golden_latest.md'
if (-not (Test-Path $jsonPath)) { Write-Host 'golden_latest.json missing' -ForegroundColor Red; exit 1 }

$r = Get-Content $jsonPath -Raw | ConvertFrom-Json
Write-Host ("pairs={0}  predBlobs={1}  unmatchedFp={2}  globalFpRate={3:P2}" -f `
    $r.PairCount, $r.TotalPredBlobs, $r.UnmatchedPredBlobs, $r.GlobalFpRate)

$md = Get-Content $mdPath -Raw
# The report's conclusion line contains U+901A U+8FC7 ("pass"). Build it from code points so
# this script stays ASCII-only and survives any console/code-page difference.
$passWord = [string]([char]0x901A) + [string]([char]0x8FC7)
if ($md -notmatch $passWord) {
    Write-Host 'GOLDEN GATE FAILED (see reports/golden_latest.md)' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'VERIFY OK' -ForegroundColor Green
