$ErrorActionPreference = 'Stop'
$releaseName = 'PivKeyUBox'
if ($args.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($args[0])) { $releaseName = $args[0] }
$root = Split-Path -Parent $PSScriptRoot
$version = '1.0.4078.44'
$expectedSha256 = 'DC4D1D9168DF26B830398303E50210B6E1729F6CE5A7AC69D2C766852F489962'
$cache = Join-Path $root '.tmp\webview2'
$packageFile = Join-Path $root '.tmp\webview2.nupkg'
$zipFile = Join-Path $root '.tmp\webview2.zip'
$release = Join-Path $root ('release\' + $releaseName)

if (-not (Test-Path (Join-Path $cache 'lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
  New-Item -ItemType Directory -Path (Split-Path $packageFile) -Force | Out-Null
  $uri = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg"
  Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $packageFile
  $actualSha256 = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
  if ($actualSha256 -ne $expectedSha256) {
    throw "WebView2 SDK checksum mismatch. Expected $expectedSha256 but received $actualSha256."
  }
  Copy-Item -LiteralPath $packageFile -Destination $zipFile -Force
  New-Item -ItemType Directory -Path $cache -Force | Out-Null
  Expand-Archive -LiteralPath $zipFile -DestinationPath $cache -Force
}

# 编译前自动停止运行中的应用进程，防止 release 目录下 dll/exe 被锁定导致编译中断
$running = Get-Process -Name 'PivKeyUBox', 'PivkeyOrganizer', 'ShellMenu' -ErrorAction SilentlyContinue
if ($running) {
  Write-Host 'Closing running PivKeyUBox/PivkeyOrganizer/ShellMenu processes before build...'
  $running | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
}

if (Test-Path $release) {
  $removed = $false
  for ($attempt = 1; $attempt -le 5; $attempt += 1) {
    try {
      Remove-Item -LiteralPath $release -Recurse -Force
      $removed = $true
      break
    } catch {
      if ($attempt -eq 5) { throw }
      Start-Sleep -Milliseconds (300 * $attempt)
    }
  }
}
New-Item -ItemType Directory -Path $release -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $release 'web') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $release 'assets') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $release 'assets\characters') -Force | Out-Null

$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
$core = Join-Path $cache 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$wpf = Join-Path $cache 'lib\net462\Microsoft.Web.WebView2.Wpf.dll'
$source = Join-Path $root 'native\PivkeyHost.cs'
$fontSource = Join-Path $root 'native\FontResources.cs'
$panelSource = Join-Path $root 'native\PanelWindow.cs'
$widgetLayerSource = Join-Path $root 'native\WidgetLayer.cs'
$layerSessionSource = Join-Path $root 'native\LayerSession.cs'
$nativeFileOpsSource = Join-Path $root 'native\NativeFileOps.cs'
$shellLauncherSource = Join-Path $root 'native\ShellLauncher.cs'
$settingsSource = Join-Path $root 'native\SettingsWindow.cs'
$managerSource = Join-Path $root 'native\Manager.cs'
$categorySource = Join-Path $root 'native\CategoryDialog.cs'
$quickSearchSource = Join-Path $root 'native\QuickSearchWindow.cs'
$noteSource = Join-Path $root 'native\NoteWindow.cs'
$confirmDialogSource = Join-Path $root 'native\ConfirmDialog.cs'
$updateManagerSource = Join-Path $root 'native\UpdateManager.cs'
$manifest = Join-Path $root 'native\PivkeyOrganizer.manifest'
$appIcon = Join-Path $root 'src\assets\icons\pivkey-organizer.ico'
$output = Join-Path $release 'PivKeyUBox.exe'
$wpfFramework = Join-Path $framework 'WPF'
$presentationCore = Join-Path $wpfFramework 'PresentationCore.dll'
$presentationFramework = Join-Path $wpfFramework 'PresentationFramework.dll'
$windowsBase = Join-Path $wpfFramework 'WindowsBase.dll'
$systemXaml = Join-Path $framework 'System.Xaml.dll'
$systemDrawing = Join-Path $framework 'System.Drawing.dll'
$systemWindowsForms = Join-Path $framework 'System.Windows.Forms.dll'
$shellMenuSource = Join-Path $root 'native\ShellMenu.cs'
$shellMenuOutput = Join-Path $release 'ShellMenu.exe'
$systemWebExtensions = Join-Path $framework 'System.Web.Extensions.dll'

& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /langversion:5 /nowarn:649 /win32manifest:$manifest /win32icon:$appIcon /out:$output `
  /reference:$presentationCore /reference:$presentationFramework /reference:$windowsBase `
  /reference:$systemXaml /reference:$systemDrawing /reference:$systemWebExtensions `
  /reference:$systemWindowsForms `
  /reference:$core /reference:$wpf $source $fontSource $panelSource $widgetLayerSource $layerSessionSource $nativeFileOpsSource $shellLauncherSource $settingsSource $managerSource $categorySource $quickSearchSource $noteSource $confirmDialogSource $updateManagerSource
if ($LASTEXITCODE -ne 0) { throw 'Windows host compilation failed.' }

# ShellMenu.exe：原生右键菜单 helper（独立进程，不依赖 WPF/WebView2，单独编译）
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /langversion:5 `
  /reference:$systemDrawing /reference:$systemWindowsForms `
  /out:$shellMenuOutput $shellMenuSource
if ($LASTEXITCODE -ne 0) { throw 'ShellMenu host compilation failed.' }

Copy-Item -LiteralPath $core -Destination $release -Force
Copy-Item -LiteralPath $wpf -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $cache 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $root 'native\PivkeyOrganizer.exe.config') -Destination (Join-Path $release 'PivKeyUBox.exe.config') -Force
Copy-Item -Path (Join-Path $root 'dist\*') -Destination (Join-Path $release 'web') -Recurse -Force
Copy-Item -LiteralPath $appIcon -Destination (Join-Path $release 'assets\pivkey-organizer.ico') -Force
Copy-Item -Path (Join-Path $root 'src\assets\characters\*.png') -Destination (Join-Path $release 'assets\characters') -Force
Copy-Item -LiteralPath (Join-Path $cache 'LICENSE.txt') -Destination (Join-Path $release 'WebView2-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $cache 'NOTICE.txt') -Destination (Join-Path $release 'WebView2-NOTICE.txt') -Force

$archive = Join-Path $root ('release\' + $releaseName + '-win-x64.zip')
if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
$archiveWritten = $false
for ($attempt = 1; $attempt -le 5; $attempt += 1) {
  try {
    Compress-Archive -Path (Join-Path $release '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $archiveWritten = $true
    break
  } catch {
    if ($attempt -eq 5) { throw }
    Start-Sleep -Milliseconds (400 * $attempt)
  }
}
if (-not $archiveWritten) { throw 'Windows package archive was not created.' }
Write-Host "Windows package: $archive"
