# ShineosQA.App v2 ランチャービルド（.NET Framework 4.x csc のみ・SDK 不要）
# WebView2 SDK DLL は v1 ビルド済みの dist\ShineosQA.App から参照する
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $root
$outDir = Join-Path $repo 'dist\ShineosQA.App'
$sdkLib = $outDir   # v1 ビルドで配置済みの WebView2 DLL をそのまま参照

if (-not (Test-Path (Join-Path $sdkLib 'Microsoft.Web.WebView2.Core.dll'))) { throw 'WebView2 DLLs not found in dist\ShineosQA.App' }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = @(
    "/r:$fw\WPF\PresentationFramework.dll",
    "/r:$fw\WPF\PresentationCore.dll",
    "/r:$fw\WPF\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll",
    "/r:$sdkLib\Microsoft.Web.WebView2.Core.dll",
    "/r:$sdkLib\Microsoft.Web.WebView2.Wpf.dll"
)

$exe = Join-Path $outDir 'ShineosQA.exe'
& $csc /nologo /target:winexe /platform:anycpu /out:$exe `
    "/win32icon:$repo\assets\app.ico" `
    (Join-Path $PSScriptRoot 'MainWindowV2.cs') `
    $refs
if ($LASTEXITCODE -ne 0) { throw 'compile failed' }
Write-Host "OK: $exe"
