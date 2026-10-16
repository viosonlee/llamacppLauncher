# Packages LlamaManager as a portable, self-contained zip.
#
# The launcher is the only thing shipped: engine binaries are large and version-bound, so the
# first run downloads them (the GUI/CLI "初始化环境" button, or --bootstrap). This keeps the
# download small and lets each engine keep its own licence.
#
# Usage:
#   pwsh -File tools/pack-release.ps1            # version = assembly version
#   pwsh -File tools/pack-release.ps1 -Version 1.2.0
param(
    [string]$Version,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'gui/LlamaManager/LlamaManager.csproj'
$publishDir = Join-Path $root 'dist'
$releaseRoot = Join-Path $root 'dist/release'

if (-not $Version) {
    $versionFile = Join-Path $root 'VERSION'
    $Version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { 'dev' }
}
Write-Host "Packaging LlamaManager $Version"

# 1. Publish the self-contained GUI (single file, .NET desktop runtime included).
Write-Host "Publishing to $publishDir ..."
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2. Assemble the portable package.
$name = "LlamaManager-$Version-win-x64"
$stage = Join-Path $releaseRoot $name
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item (Join-Path $publishDir 'LlamaManager.exe') $stage
Copy-Item (Join-Path $root 'llama-manager.cmd') $stage
Copy-Item (Join-Path $root 'llama-manager.ps1') $stage
Copy-Item (Join-Path $root 'README.md') $stage
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

# A first-run note: engines are downloaded on demand, not bundled.
$readme = @"
LlamaManager $Version (Windows x64, portable)

1. 解压到任意目录，双击 LlamaManager.exe。
2. 在顶部选择引擎，点「初始化环境」——会自动下载该引擎的二进制与 Python 依赖。
   （llama.cpp / Strata / PolyStrata 均可自动获取；需要联网与 NVIDIA 驱动。）
3. 在「模型」页选择已下载的 GGUF，点「启动服务」。
4. 用「打开页面」或把接入地址（http://127.0.0.1:<port>/v1）交给 pi / Claude Code / Codex。

模型文件请自行准备（.gguf）。引擎二进制首次使用时下载，不随本包分发。
"@
Set-Content -Path (Join-Path $stage '首次运行.txt') -Value $readme -Encoding UTF8

# 3. Zip it.
if (-not $NoZip) {
    $zip = Join-Path $releaseRoot "$name.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Write-Host "Compressing $zip ..."
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Package: $zip"
}

Write-Host "Done. Staged at $stage"
