$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> 1. 构建 Windows 便携产物..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw "构建便携版失败" }

Write-Host "==> 2. 定位 Inno Setup 编译器 (ISCC)..." -ForegroundColor Cyan
$isccCandidates = @(
  (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
  (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)

$iscc = $null
foreach ($candidate in $isccCandidates) {
  if ($candidate -and (Test-Path $candidate)) {
    $iscc = $candidate
    break
  }
}

if (-not $iscc) {
  throw "未找到 Inno Setup 编译器 ISCC.exe。请先安装 Inno Setup 6，例如运行: winget install JRSoftware.InnoSetup"
}
Write-Host "找到 Inno Setup 编译器: $iscc" -ForegroundColor Green

$pkgJson = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'package.json') | ConvertFrom-Json
$appVersion = $pkgJson.version
if (-not $appVersion) { $appVersion = '0.1.0' }

Write-Host "==> 3. 正在生成 Inno Setup 安装包 (版本: v$appVersion)..." -ForegroundColor Cyan
$issFile = Join-Path $root 'scripts\installer.iss'

& $iscc "/DMyAppVersion=$appVersion" $issFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }

$installerPath = Join-Path $root "release\PivKeyUBox-Setup-v$appVersion.exe"
if (Test-Path $installerPath) {
  $fileInfo = Get-Item $installerPath
  $sizeMb = [Math]::Round($fileInfo.Length / 1MB, 2)
  $sha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
  Write-Host "========================================" -ForegroundColor Green
  Write-Host "安装包生成成功！" -ForegroundColor Green
  Write-Host "文件路径: $installerPath" -ForegroundColor Green
  Write-Host "文件大小: $sizeMb MB" -ForegroundColor Green
  Write-Host "SHA256:   $sha256" -ForegroundColor Green
  Write-Host "========================================" -ForegroundColor Green
} else {
  throw "未在预期位置找到生成的安装包: $installerPath"
}
