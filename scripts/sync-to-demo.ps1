# 将当前仓库内容同步到本机 E:\demo\DeepPCB-Station（若脚本已在该目录则 no-op 提示）
param(
    [string]$Target = "E:\demo\DeepPCB-Station"
)

$ErrorActionPreference = "Stop"
$src = Resolve-Path (Join-Path $PSScriptRoot "..")
Write-Host "Source: $src"
Write-Host "Target: $Target"

if ((Resolve-Path $src).Path -eq (Resolve-Path -LiteralPath $Target -ErrorAction SilentlyContinue)) {
    Write-Host "已在目标目录，无需同步。"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
robocopy $src $Target /E /XD .git bin obj results reports .vs `
    /XF *.user *.suo /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
$code = $LASTEXITCODE
if ($code -ge 8) {
    Write-Error "robocopy failed with code $code"
    exit $code
}
Write-Host "同步完成 → $Target (robocopy code=$code)"
