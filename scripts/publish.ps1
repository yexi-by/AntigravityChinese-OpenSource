param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ('.local/publish-' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $root 'release'
$package = Join-Path $stage 'AntigravityChinese-win-x64'
New-Item -ItemType Directory -Path $package -Force | Out-Null
dotnet publish (Join-Path $root 'src/AntigravityChinese/AntigravityChinese.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'build') -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
Copy-Item -LiteralPath (Join-Path $stage 'build/AntigravityChinese.exe') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root '使用说明.txt') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $package -Recurse
New-Item -ItemType Directory -Path $release -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $package 'AntigravityChinese.exe') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $package '使用说明.txt') -Destination $release -Force
$zip = Join-Path $release 'AntigravityChinese-win-x64.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
$files = @((Join-Path $release 'AntigravityChinese.exe'), $zip)
$hashes = $files | ForEach-Object { $hash=Get-FileHash -LiteralPath $_ -Algorithm SHA256; $hash.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $files | Select-Object FullName,Length
