# ShineosQA.App ビルドスクリプト
# - .NET Framework 4.x の csc.exe のみでビルド（SDK 不要）
# - WebView2 SDK (Microsoft.Web.WebView2) を NuGet から取得
# - 出力: dist\ShineosQA.App\ShineosQA.exe
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $root
$outDir = Join-Path $repo 'dist\ShineosQA.App'
$tmpDir = Join-Path $env:TEMP 'shineos-webview2-sdk'
$sdkVer = '1.0.4129.50'
$sdkPkg = Join-Path $tmpDir "microsoft.web.webview2.$sdkVer.nupkg"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- WebView2 SDK の取得（初回のみ） ---
if (-not (Test-Path (Join-Path $tmpDir "lib\net462\Microsoft.Web.WebView2.Core.dll"))) {
    New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdkVer/microsoft.web.webview2.$sdkVer.nupkg"
    Write-Host "downloading WebView2 SDK $sdkVer ..."
    & curl.exe -s --max-time 300 -o $sdkPkg $url
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $sdkPkg)) { throw 'WebView2 SDK download failed' }
    Copy-Item $sdkPkg (Join-Path $tmpDir 'pkg.zip') -Force
    Expand-Archive (Join-Path $tmpDir 'pkg.zip') (Join-Path $tmpDir 'pkg') -Force
}

$sdkLib = Join-Path $tmpDir 'pkg\lib\net462'
$native = Join-Path $tmpDir 'pkg\runtimes\win-x64\native\WebView2Loader.dll'

# --- csc でビルド（.NET Framework 4.x WPF） ---
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = @(
    "/r:$fw\WPF\PresentationFramework.dll",
    "/r:$fw\WPF\PresentationCore.dll",
    "/r:$fw\WPF\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll",
    "/r:$fw\System.ServiceProcess.dll",
    "/r:$sdkLib\Microsoft.Web.WebView2.Core.dll",
    "/r:$sdkLib\Microsoft.Web.WebView2.Wpf.dll"
)

$exe = Join-Path $outDir 'ShineosQA.exe'
& $csc /nologo /target:winexe /platform:anycpu /out:$exe `
    "/win32icon:$repo\assets\app.ico" `
    (Join-Path $PSScriptRoot 'MainWindow.cs') `
    $refs
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }

# --- 実行時 DLL を配置 ---
Copy-Item (Join-Path $sdkLib 'Microsoft.Web.WebView2.Core.dll') $outDir -Force
Copy-Item (Join-Path $sdkLib 'Microsoft.Web.WebView2.Wpf.dll') $outDir -Force
Copy-Item $native $outDir -Force

Write-Host "OK: $exe"
