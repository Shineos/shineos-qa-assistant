# クリーン起動スモーク: 公式プリビルドエンジン+publishedバックエンドの「起動配線」を機械検知する。
# 位置づけ: 過去2回のStore認定落ち(CRT欠落→engine-down、VC++未開示)の回帰をリリースCIで検知するためのゲート。
# 注意: GitHub runner は VC++ 再配布済みのため「CRT無しPC」は完全再現できない(最終検証はStore審査)。
#       本スモークが検知するのは (1)CRT同梱の解除 (2)エンジン実行不可 (3)バックエンド起動・/health・/api/status不良。
param(
  [string]$EngineDir = "spikes/phase0/engine/cpu",
  [string]$BackendDir = "output/backend-pub"
)
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repo

function Fail($msg) { Write-Host "::error::smoke-cleanroom: $msg"; exit 1 }

# (a) CRT 3種が installer/crt に同梱されていること(.iss が {app}\engine へコピーする)
foreach ($f in @("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")) {
  if (-not (Test-Path (Join-Path "installer/crt" $f))) { Fail "installer/crt/$f が無い — CRT同梱が解除された疑い" }
}
Write-Host "ok: installer/crt に CRT 3種あり"

# (b) エンジンexeが CRT に動的リンクしていること = 同梱が必須であることの証明。
#     dumpbin が見つかる環境でのみ実施(無い場合は skip — 他のゲートで代替する)
# 注意: ネイティブexeはstderrに正常出力することがあるため、(b)(c)の区間だけ EAP を緩める
#      (PS5.1はEAP=Stopだとstderr出力がNativeCommandError例外化する)
$llama = Join-Path $EngineDir "llama-server.exe"
if (-not (Test-Path $llama)) { Fail "llama-server.exe が無い: $EngineDir" }
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$ErrorActionPreference = "Continue"
if (Test-Path $vswhere) {
  $dumpbin = (& $vswhere -latest -products * -find "**\dumpbin.exe" | Select-Object -First 1)
  if ($dumpbin) {
    $imports = (& $dumpbin /dependents $llama 2>$null | Out-String)
    if ($imports -match "VCRUNTIME140\.dll|MSVCP140\.dll") {
      Write-Host "ok: engine は CRT を動的リンク(同梱が必須)"
    } else {
      Write-Host "warn: engine の依存に VCRUNTIME/MSVCP が無い — 静的リンクに変わった可能性(同梱は不要だが要確認)"
    }
  } else {
    Write-Host "skip: dumpbin 不在 — 依存解析をスキップ"
  }
} else {
  Write-Host "skip: vswhere 不在 — 依存解析をスキップ"
}

# (c) エンジン単体起動(バージョン表示で即終了。llama-serverはバージョンをstderrに出す)
& $llama --version 2>&1 | Out-Null
$llamaExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($llamaExit -ne 0) { Fail "llama-server.exe --version 失敗 (exit=$llamaExit) — エンジンが実行不可" }
Write-Host "ok: llama-server.exe --version exit=0"

# (d) publishedバックエンド起動 → /health + /api/status
$inUse = Get-NetTCPConnection -LocalPort 8300 -State Listen -ErrorAction SilentlyContinue
if ($inUse) { Fail "ポート8300が既に使用中 — 別インスタンス(インストール済みアプリ等)を停止してから実行してください" }
$backend = Join-Path $BackendDir "ShineosQA.Backend.exe"
if (-not (Test-Path $backend)) { Fail "バックエンドが未publish: $backend" }
$proc = Start-Process -FilePath $backend -PassThru -WindowStyle Hidden
try {
  $healthy = $false
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    try {
      $h = Invoke-RestMethod "http://127.0.0.1:8300/health" -TimeoutSec 2
      if ($h.status) { $healthy = $true; break }
    } catch { }
  }
  if (-not $healthy) { Fail "バックエンド /health が応答しない(起動失敗またはポート8300競合)" }
  $status = Invoke-RestMethod "http://127.0.0.1:8300/api/status" -TimeoutSec 5
  if (-not $status.version) { Fail "/api/status の応答に version が無い" }
  Write-Host "ok: backend /health + /api/status (version=$($status.version))"
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
Write-Host "smoke-cleanroom: ALL OK"
