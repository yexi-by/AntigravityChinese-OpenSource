param(
    [ValidateSet('Check','Update')][string]$Mode = 'Check',
    [string]$AppPath,
    [string]$Profile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path '.local' | Out-Null
    dotnet build tools/AntigravityChinese.Maintainer -c Release --nologo --verbosity quiet
    $maintainer = Join-Path $root 'tools/AntigravityChinese.Maintainer/bin/Release/net10.0-windows/AntigravityChinese.Maintainer.exe'
    $appArgs = if ($AppPath) { @('--app', $AppPath) } else { @() }
    & $maintainer check @appArgs --output .local/maintenance-check.json
    $decision = Get-Content -LiteralPath .local/maintenance-check.json -Raw | ConvertFrom-Json
    if (-not $decision.NeedsUpdate -or $Mode -eq 'Check') { return }
    if ($decision.Notices.Count -gt 0) { throw '发现资源或加载结构变化。请先根据上方提示修正适配，再继续翻译；当前安装未修改。' }
    $att = Join-Path $root '.local/att/att.exe'
    if (-not (Test-Path -LiteralPath $att)) { throw '请先执行 scripts/setup-att.ps1 准备本项目的 ATT。' }
    & $maintainer extract @appArgs
    & $maintainer att-resources
    if (-not (Test-Path -LiteralPath '.local/att/projects/generic/AntigravityChinese/project.db')) {
        & $att generic init --name AntigravityChinese --path (Join-Path $root 'translation/input') --source-language en --target-language zh-CN
    }
    & $att generic extract --name AntigravityChinese
    & $maintainer seed-att --output .local/released-manual.toml
    & $att generic manual apply --name AntigravityChinese .local/released-manual.toml
    $profileArgs = if ($Profile) { @($Profile) } else { @() }
    & $att generic translate --name AntigravityChinese @profileArgs --terms translation/terminology.toml --placeholders translation/placeholders.toml
    & $att generic translation export --name AntigravityChinese .local/translations.jsonl
    & $att generic manual export --name AntigravityChinese --selection pending .local/pending.toml
    & $att generic manual export --name AntigravityChinese --selection rejected .local/rejected.toml
    & $att generic write-back --name AntigravityChinese
    & $maintainer import-att --write-back .local/att/projects/generic/AntigravityChinese/write_back
    & $maintainer check @appArgs
    Write-Output '已更新翻译资产。请审校 .local 中的导出、补译和拒绝项，并完成相关界面验证后再提交或发布。'
} finally { Pop-Location }
