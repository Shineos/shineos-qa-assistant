param(
    [string]$Version = "2.0.3.0",
    [string]$Publisher = "CN=Shineos Inc.",
    [string]$Repo = "D:\dev\shineos-local-ai",
    [string]$Tools = "$env:LOCALAPPDATA\Temp\msixtools\pkg\bin\10.0.26100.0\x64"
)
# MSIXビルド（ローカル検証用・自己署名。Store提出時はStoreが再署名するため署名は検証用）
# 構成: ラッパー(ShineosQA.exe)＋バックエンド自己完結publish＋llama.cppエンジン＋モデル3点＋licenses
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$stage = Join-Path $Repo 'output\msix-stage'
$msix = Join-Path $Repo ("dist\ShineosQA-" + $Version + '.msix')

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# ---- 1) バックエンド自己完結publish（パッケージルート＝バックエンドのBaseDirectory） ----
if (-not (Test-Path (Join-Path $Repo 'output\backend-pub\ShineosQA.Backend.exe'))) { throw 'backend-pub missing (run dotnet publish first)' }
Copy-Item (Join-Path $Repo 'output\backend-pub\*') $stage -Recurse -Force

# ---- 2) WebView2ラッパー ----
foreach ($f in 'ShineosQA.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll') {
    Copy-Item (Join-Path $Repo ("dist\ShineosQA.App\" + $f)) $stage -Force
}

# ---- 3) エンジン・モデル・ライセンス ----
Copy-Item (Join-Path $Repo 'spikes\phase0\engine\cpu\*') (Join-Path $stage 'engine') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $stage 'models') | Out-Null
foreach ($m in 'Qwen3-1.7B-IQ4_XS.gguf', 'bge-m3-Q8_0.gguf', 'bge-reranker-v2-m3-Q8_0.gguf') {
    Copy-Item (Join-Path $Repo ("spikes\phase0\models\" + $m)) (Join-Path $stage 'models') -Force
}
Copy-Item (Join-Path $Repo 'vendor\licenses\*') (Join-Path $stage 'licenses') -Recurse -Force
Copy-Item (Join-Path $Repo 'vendor\THIRD-PARTY-NOTICES.txt') $stage -Force
New-Item -ItemType Directory -Path (Join-Path $stage 'assets') -Force | Out-Null
Copy-Item (Join-Path $Repo 'assets\app.ico') (Join-Path $stage 'assets\app.ico') -Force

# ---- 4) ロゴ生成（app.icoから必要サイズ） ----
Add-Type -AssemblyName System.Drawing
$assetsDir = Join-Path $stage 'assets'
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
# 300pxストアロゴPNGから各サイズへ縮小生成（app.icoは32pxまでしか含まないため）
$src = [System.Drawing.Bitmap]::FromFile((Join-Path $Repo 'assets\store-logo-300.png'))
if ($src.Width -lt 100) { throw ('logo too small: ' + $src.Width) }
foreach ($size in @(150, 44, 50)) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $name = if ($size -eq 150) { 'Square150x150Logo.png' } elseif ($size -eq 44) { 'Square44x44Logo.png' } else { 'StoreLogo.png' }
    $bmp.Save((Join-Path $assetsDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$src.Dispose()

# ---- 5) マニフェスト（バージョン・Publisher注入・日本語はmakeappx検証で落ちるため英字化） ----
# Store掲載の DisplayName/Description はPartner Centerで言語ごとに上書きされるため、
# マニフェスト自体は英字で問題ない
$manifest = Get-Content (Join-Path $Repo 'installer\msix\AppxManifest.xml') -Raw -Encoding UTF8
$manifest = $manifest -replace 'Version="2\.0\.3\.0"', ('Version="' + $Version + '"')
$manifest = $manifest -replace 'Publisher="CN=Shineos Inc\."', ('Publisher="' + $Publisher + '"')
$manifest = $manifest -replace '<DisplayName>[^<]+</DisplayName>', '<DisplayName>ShineosQA</DisplayName>'
$manifest = $manifest -replace '<PublisherDisplayName>[^<]+</PublisherDisplayName>', '<PublisherDisplayName>ShineosQA</PublisherDisplayName>'
$manifest = $manifest -replace 'Description="[^"]*"', 'Description="ShineosQA internal QA tool"'
$manifest = $manifest -replace 'DisplayName="[^"]*"', 'DisplayName="ShineosQA"'
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

# ---- 6) パック ----
if (Test-Path $msix) { Remove-Item $msix -Force }
& (Join-Path $Tools 'makeappx.exe') pack /d $stage /p $msix /nv
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
Write-Output ('msix: ' + $msix + ' (' + [math]::Round((Get-Item $msix).Length/1GB, 2) + ' GB)')

# ---- 7) 検証用に自己署名（Store提出時は不要・Storeが置換する） ----
# 既存証明書を再利用（毎回新規作成すると証明書ストアの再导入が必要になるため）
$cerPath = Join-Path $Repo 'dist\ShineosQA-msix-test.cer'
$pfxPath = "$env:TEMP\msix-test.pfx"
$existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq 'ShineosQA MSIX local-test' }
if ($existing -and (Test-Path $pfxPath) -and (Test-Path $cerPath)) {
    Write-Output 'reusing existing test certificate'
} else {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName 'ShineosQA MSIX local-test' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    [IO.File]::WriteAllBytes($cerPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    [IO.File]::WriteAllBytes($pfxPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'msixtest'))
    Write-Output 'created new test certificate (import to LocalMachine\TrustedPeople+Root for install testing)'
}
& (Join-Path $Tools 'signtool.exe') sign /fd SHA256 /f $pfxPath /p msixtest $msix
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
Write-Output ('signed: ' + $msix)
Write-Output 'NOTE: Store re-signs on upload - this local signature is for install testing only.'
