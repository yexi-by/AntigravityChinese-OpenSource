param(
    [Parameter(Mandatory)][string]$AttRoot,
    [string]$ConfigurationPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$project = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $AttRoot).Path
$runtime = [IO.Path]::GetFullPath((Join-Path $project '.local/att'))
if ($source.TrimEnd('\','/') -eq $runtime.TrimEnd('\','/')) { throw 'ATT 来源目录必须与本项目的本机运行目录分离。' }
$activeConfig = Join-Path $runtime 'config.toml'
$configSource = if ($ConfigurationPath) { (Resolve-Path -LiteralPath $ConfigurationPath).Path }
    elseif (Test-Path -LiteralPath $activeConfig) { $activeConfig }
    elseif (Test-Path -LiteralPath (Join-Path $source 'config.toml')) { Join-Path $source 'config.toml' }
    else { throw '请通过 -ConfigurationPath 指定已配置的 ATT config.toml；不要使用示例配置。' }
foreach ($example in @((Join-Path $source 'config.example.toml'), (Join-Path $runtime 'config.example.toml'))) {
    if ((Test-Path -LiteralPath $example) -and (Get-FileHash -LiteralPath $example).Hash -eq (Get-FileHash -LiteralPath $configSource).Hash) {
        throw '指定的配置仍是 ATT 示例，请先填好服务配置。'
    }
}
$executable = @('target/x86_64-pc-windows-msvc/release/att.exe', 'target/release/att.exe', 'att.exe') |
    ForEach-Object { Join-Path $source $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $executable) { throw '没有找到 att.exe，请先构建或解压完整 ATT 发行包。' }
$manifest = Join-Path $source 'Cargo.toml'
if (Test-Path -LiteralPath $manifest) {
    $expected = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
    $actual = (& $executable --version) -join ''
    if (-not $actual.EndsWith($expected)) { throw "ATT 可执行文件与源码版本不同，请先在 ATT 仓库执行 cargo build --locked --release --target x86_64-pc-windows-msvc。" }
}
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $runtime 'att.exe') -Force
if (Test-Path -LiteralPath (Join-Path $source 'scripts/sync-dist-resources.ps1')) {
    & (Join-Path $source 'scripts/sync-dist-resources.ps1') -TargetRoot $runtime
} else {
    foreach ($name in @('docs', 'prompts', 'skills', 'licenses', 'README.md', 'LICENSE', 'config.example.toml')) {
        $item = Join-Path $source $name
        if (-not (Test-Path -LiteralPath $item)) { throw "ATT 发行包缺少 $name" }
        $destination = Join-Path $runtime $name
        if (Test-Path -LiteralPath $item -PathType Container) {
            New-Item -ItemType Directory -Force -Path $destination | Out-Null
            Get-ChildItem -LiteralPath $item -Force | Copy-Item -Destination $destination -Recurse -Force
        } else { Copy-Item -LiteralPath $item -Destination $destination -Force }
    }
}
if ([IO.Path]::GetFullPath($configSource) -ne [IO.Path]::GetFullPath($activeConfig)) {
    Copy-Item -LiteralPath $configSource -Destination $activeConfig -Force
}
$system = Join-Path $runtime 'prompts/translation/system.md'
$style = Get-Content -LiteralPath (Join-Path $project 'translation/style-guide.md') -Raw
$base = Get-Content -LiteralPath $system -Raw
[IO.File]::WriteAllText($system, $base + "`n`n" + $style, [Text.UTF8Encoding]::new($false))
Write-Output 'ATT 已准备；配置只保存在 .local/att/config.toml，不进入仓库或发布包。'
& (Join-Path $runtime 'att.exe') --version
