# perf-suite.ps1 - ShineosQA v2 / Phase 0 性能計測スイート
#
# 目的: /api/chat の SSE を curl で受け、質問×複数回の TTFT（最初の1文字まで）と
#       合計時間を計測し、質問ごと・全体の p50/p95/平均/最小/最大を JSON と表で出す。
# 方式: 既存 measure.ps1 と同じ「UI と同一の /api/chat SSE を curl.exe で受ける」方式。
#       サーバ側計測値 done.ttfb_ms / done.ms を採用し、クライアント実測も wall_ms として併記する。
# 互換: Windows PowerShell 5.1（三項演算子・?? は使わない。本体は UTF-8 BOM で保存すること）。
#
# 使い方（例）:
#   powershell -NoProfile -ExecutionPolicy Bypass -File perf-suite.ps1 -Label cold-dify -Runs 3
#   powershell -NoProfile -ExecutionPolicy Bypass -File perf-suite.ps1 -Runs 5 -AllCold -OutFile C:\tmp\perf.json
#   powershell -NoProfile -ExecutionPolicy Bypass -File perf-suite.ps1 -SelfTest
#
# 前提: バックエンドが起動していること（既定 http://127.0.0.1:8300）。
#       未起動の場合は理由を表示して終了コード 3 で終了する（誤った数値を出さない）。
#
# 終了コード: 0=全サンプル成功 / 2=一部サンプル失敗（JSONは保存） / 3=前提エラー（未起動・質問ファイル不正）

param(
  [string]$BaseUrl = "http://127.0.0.1:8300",
  [string]$QuestionsFile = "questions.json",
  [int]$Runs = 3,
  [string]$Label = "perf",
  [string]$OutFile = "",
  [switch]$AllCold,
  [int]$TimeoutSec = 600,
  [switch]$KeepChats,
  [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = $BaseUrl.TrimEnd('/')
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$utf8Bom = New-Object Text.UTF8Encoding($true)

# ---------------------------------------------------------------------------
# 共通ヘルパー
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) {
  Write-Output ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg)
}

function Write-Fatal([string]$msg) {
  Write-Output ("ERROR: " + $msg)
  Write-Output ("HINT : バックエンドを起動してから実行してください。例: `"$env:LOCALAPPDATA\Programs\ShineosQA\launch.vbs`" または ShineosQA.Backend.exe --config config.json")
  Write-Output ("HINT : 既定の待受は http://127.0.0.1:8300 です。別ポートなら -BaseUrl で指定してください。")
  exit 3
}

function Get-Prop($obj, [string]$name) {
  # PS5.1 では存在しないプロパティ参照が $null になるだけで例外にならないが、
  # JSON の欠落フィールドを明示的に扱うため存在確認してから返す。
  if ($null -eq $obj) { return $null }
  $p = $obj.PSObject.Properties[$name]
  if ($null -eq $p) { return $null }
  return $p.Value
}

function Get-Percentile([double[]]$Sorted, [double]$P) {
  # 最近順位法（nearest-rank）。n=1 でも安全。
  $n = $Sorted.Length
  if ($n -eq 0) { return $null }
  $rank = [int][math]::Ceiling(($P / 100.0) * $n)
  if ($rank -lt 1) { $rank = 1 }
  if ($rank -gt $n) { $rank = $n }
  return $Sorted[$rank - 1]
}

function Get-Stats([double[]]$Values) {
  $vals = @($Values | Where-Object { $null -ne $_ })
  $n = $vals.Count
  if ($n -eq 0) {
    return [pscustomobject][ordered]@{ n = 0; mean = $null; min = $null; max = $null; p50 = $null; p95 = $null }
  }
  $arr = [double[]]::new($n)
  for ($i = 0; $i -lt $n; $i++) { $arr[$i] = [double]$vals[$i] }
  [array]::Sort($arr)
  $sum = 0.0
  foreach ($v in $arr) { $sum += $v }
  return [pscustomobject][ordered]@{
    n    = $n
    mean = [math]::Round($sum / $n, 1)
    min  = [math]::Round($arr[0], 1)
    max  = [math]::Round($arr[$n - 1], 1)
    p50  = [math]::Round((Get-Percentile $arr 50.0), 1)
    p95  = [math]::Round((Get-Percentile $arr 95.0), 1)
  }
}

function Format-Sec($ms) {
  if ($null -eq $ms) { return "-" }
  return ([math]::Round(([double]$ms) / 1000.0, 2)).ToString("0.00")
}

function Format-SecUnit($ms) {
  $t = Format-Sec $ms
  if ($t -eq "-") { return "-" }
  return ($t + "s")
}

# ---------------------------------------------------------------------------
# -SelfTest: 集計ロジックだけをネットワークなしで検証（回帰確認用）
# ---------------------------------------------------------------------------
if ($SelfTest) {
  $ok = $true
  $s10 = Get-Stats ([double[]]@(1,2,3,4,5,6,7,8,9,10))
  if ($s10.n -ne 10) { $ok = $false; Write-Output "FAIL n=$($s10.n)" }
  if ($s10.min -ne 1) { $ok = $false; Write-Output "FAIL min=$($s10.min)" }
  if ($s10.max -ne 10) { $ok = $false; Write-Output "FAIL max=$($s10.max)" }
  if ($s10.mean -ne 5.5) { $ok = $false; Write-Output "FAIL mean=$($s10.mean)" }
  if ($s10.p50 -ne 5) { $ok = $false; Write-Output "FAIL p50=$($s10.p50)" }
  if ($s10.p95 -ne 10) { $ok = $false; Write-Output "FAIL p95=$($s10.p95)" }
  $s3 = Get-Stats ([double[]]@(100,200,300))
  if ($s3.p50 -ne 200) { $ok = $false; Write-Output "FAIL p50(3)=$($s3.p50)" }
  if ($s3.p95 -ne 300) { $ok = $false; Write-Output "FAIL p95(3)=$($s3.p95)" }
  if ($s3.mean -ne 200) { $ok = $false; Write-Output "FAIL mean(3)=$($s3.mean)" }
  $s0 = Get-Stats ([double[]]@())
  if ($s0.n -ne 0 -or $null -ne $s0.p50) { $ok = $false; Write-Output "FAIL empty n=$($s0.n) p50=$($s0.p50)" }
  $one = Get-Stats ([double[]]@(42))
  if ($one.p50 -ne 42 -or $one.p95 -ne 42) { $ok = $false; Write-Output "FAIL one p50=$($one.p50) p95=$($one.p95)" }
  if ($ok) { Write-Output "SELFTEST PASS (n=10/n=3/n=0/n=1, nearest-rank p50/p95, mean/min/max)"; exit 0 }
  Write-Output "SELFTEST FAIL"; exit 1
}

# ---------------------------------------------------------------------------
# 1) 入力解決（questions.json はスクリプトと同じフォルダを既定に解決）
# ---------------------------------------------------------------------------
if (-not [IO.Path]::IsPathRooted($QuestionsFile)) {
  $cand = Join-Path $scriptDir $QuestionsFile
  if (Test-Path $cand) { $QuestionsFile = $cand }
}
if (-not (Test-Path $QuestionsFile)) {
  Write-Fatal ("質問ファイルが見つかりません: " + $QuestionsFile)
}

$rawJson = [IO.File]::ReadAllText($QuestionsFile, [Text.Encoding]::UTF8)
try { $parsed = $rawJson | ConvertFrom-Json } catch { Write-Fatal ("質問ファイルのJSON解析に失敗: " + $_.Exception.Message) }
if ($null -eq $parsed) { Write-Fatal "質問ファイルが空です" }
if ($parsed -is [System.Management.Automation.PSCustomObject]) {
  Write-Fatal ("質問ファイルの形式が不正です（JSONルートは配列である必要があります）: " + $QuestionsFile + " / scenarios-100.json は expect/refuse/refusekw を持つ別形式です。docs/perf-suite.md の変換例を参照してください。")
}
$questions = @($parsed)
if ($questions.Count -lt 1) { Write-Fatal "質問が0件です" }
for ($i = 0; $i -lt $questions.Count; $i++) {
  if ($null -eq (Get-Prop $questions[$i] "q")) {
    Write-Fatal ("questions[" + $i + "] に q がありません（questions.json 形式は tag/q）。scenarios-100.json 形式は未対応です。")
  }
}

if ($Runs -lt 1) { Write-Fatal "-Runs は 1 以上を指定してください" }

# ---------------------------------------------------------------------------
# 2) 起動確認（未起動なら明確なエラーで終了）
# ---------------------------------------------------------------------------
Write-Step ("checking backend: " + $base + "/health")
$health = $null
try { $health = Invoke-RestMethod -Uri ($base + "/health") -Method Get -TimeoutSec 5 } catch { $health = $null }
if ($null -eq $health -or -not (Get-Prop $health "status")) {
  Write-Fatal ("バックエンドが応答しません（/health 失敗）: " + $base)
}

$status = $null
try { $status = Invoke-RestMethod -Uri ($base + "/api/status") -Method Get -TimeoutSec 10 } catch { $status = $null }
$settings = $null
try { $settings = Invoke-RestMethod -Uri ($base + "/api/settings") -Method Get -TimeoutSec 10 } catch { $settings = $null }

Write-Step ("backend version=" + (Get-Prop $status "version") + " tier=" + (Get-Prop $status "tier") + " chunks=" + (Get-Prop $status "chunks"))
if ($null -ne $settings) {
  Write-Step ("settings web_search=" + (Get-Prop $settings "web_search") + " bg_friendly=" + (Get-Prop $settings "bg_friendly") + " idle_unload_minutes=" + (Get-Prop $settings "idle_unload_minutes"))
}

# ---------------------------------------------------------------------------
# 3) SSE 呼び出し
# ---------------------------------------------------------------------------
function Clear-AnswerCache {
  try { & curl.exe -s --noproxy "*" --max-time 30 -X POST ($base + "/api/cache-clear") | Out-Null } catch { }
}

function Invoke-Ask([string]$Question, [string]$Tag, [string]$Uuid) {
  $body = @{ chat_uuid = $Uuid; message = $Question } | ConvertTo-Json -Compress
  $tmp = [IO.Path]::GetTempFileName()
  $out = [IO.Path]::GetTempFileName()
  $sw = [Diagnostics.Stopwatch]::StartNew()
  [IO.File]::WriteAllText($tmp, $body, $utf8NoBom)
  & curl.exe -s -N --noproxy "*" --max-time $TimeoutSec -X POST ($base + "/api/chat") -H "Content-Type: application/json; charset=utf-8" --data-binary "@$tmp" -o $out
  $curlExit = $LASTEXITCODE
  $sw.Stop()

  $res = [ordered]@{
    ok             = $false
    error          = ""
    chat_uuid      = $Uuid
    answer_chars   = 0
    cached         = $null
    guard          = $null
    kind           = $null
    server_ttfb_ms = $null
    server_total_ms = $null
    client_wall_ms = [double]$sw.ElapsedMilliseconds
  }

  if ($curlExit -ne 0) {
    $res.error = "curl_exit_" + $curlExit
    Remove-Item $tmp, $out -Force -ErrorAction SilentlyContinue
    return [pscustomobject]$res
  }

  $lines = [IO.File]::ReadAllLines($out, [Text.Encoding]::UTF8)
  $ev = ""
  $done = $null
  $errData = ""
  $answer = New-Object Text.StringBuilder
  $metaUuid = ""
  foreach ($ln in $lines) {
    if ($ln.StartsWith("event: ")) { $ev = $ln.Substring(7).Trim() }
    elseif ($ln.StartsWith("data: ")) {
      $json = $ln.Substring(6)
      if ($ev -eq "delta") {
        try { $o = $json | ConvertFrom-Json; [void]$answer.Append([string](Get-Prop $o "content")) } catch { }
      }
      elseif ($ev -eq "meta") {
        try {
          $m = $json | ConvertFrom-Json
          $mu = [string](Get-Prop $m "chat_uuid")
          if ($mu) { $metaUuid = $mu }
        } catch { }
      }
      elseif ($ev -eq "done") {
        try { $done = $json | ConvertFrom-Json } catch { $done = $null }
      }
      elseif ($ev -eq "error") {
        $errData = $json
      }
    }
  }
  if ($metaUuid) { $res.chat_uuid = $metaUuid }
  $res.answer_chars = $answer.Length

  if ($errData) {
    $res.error = "sse_error"
    $res.server_total_ms = $null
  }
  elseif ($null -eq $done) {
    $res.error = "no_done_event"
  }
  else {
    $res.ok = $true
    $res.cached = Get-Prop $done "cached"
    $res.guard = Get-Prop $done "guard"
    $res.kind = Get-Prop $done "kind"
    $ttfb = Get-Prop $done "ttfb_ms"
    $total = Get-Prop $done "ms"
    if ($null -ne $ttfb) { $res.server_ttfb_ms = [double]$ttfb }
    if ($null -ne $total) { $res.server_total_ms = [double]$total }
  }
  Remove-Item $tmp, $out -Force -ErrorAction SilentlyContinue
  return [pscustomobject]$res
}

# ---------------------------------------------------------------------------
# 4) 計測
# ---------------------------------------------------------------------------
$samples = @()
$createdChats = @()
Write-Step ("measuring " + $questions.Count + " question(s) x " + $Runs + " run(s) (all_cold=" + [string]([bool]$AllCold) + ")")

for ($qi = 0; $qi -lt $questions.Count; $qi++) {
  $item = $questions[$qi]
  $tag = [string](Get-Prop $item "tag")
  if (-not $tag) { $tag = "q" + ($qi + 1) }
  $question = [string](Get-Prop $item "q")
  for ($run = 1; $run -le $Runs; $run++) {
    $isCold = $false
    if ($run -eq 1 -or $AllCold) { $isCold = $true }
    $phase = "warm"
    if ($isCold) { $phase = "cold" }
    if ($isCold) { Clear-AnswerCache }
    $uuid = [Guid]::NewGuid().ToString('N')
    $r = Invoke-Ask $question $tag $uuid
    if ($r.chat_uuid -and -not $KeepChats) { $createdChats += $r.chat_uuid }
    $sample = [pscustomobject][ordered]@{
      tag             = $tag
      question        = $question
      run             = $run
      phase           = $phase
      ok              = $r.ok
      error           = $r.error
      cached          = $r.cached
      guard           = $r.guard
      kind            = $r.kind
      server_ttfb_ms  = $r.server_ttfb_ms
      server_total_ms = $r.server_total_ms
      client_wall_ms  = $r.client_wall_ms
      answer_chars    = $r.answer_chars
    }
    $samples += $sample
    Write-Step ("  " + $tag + " run=" + $run + " " + $phase + " ok=" + $r.ok + " cached=" + [string]$r.cached + " guard=" + [string]$r.guard + " ttfb=" + (Format-SecUnit $r.server_ttfb_ms) + " total=" + (Format-SecUnit $r.server_total_ms))
    if (-not $r.ok) { Write-Step ("    error=" + $r.error) }
  }
}

# 後片付け（作成したテストチャットを削除。失敗しても計測結果は残す）
if (-not $KeepChats) {
  $removed = 0
  foreach ($u in ($createdChats | Select-Object -Unique)) {
    if ($u) {
      try { & curl.exe -s --noproxy "*" --max-time 15 --output NUL -X DELETE ($base + "/api/chats/" + $u) | Out-Null; $removed++ } catch { }
    }
  }
  Write-Step ("cleanup: removed " + $removed + " test chat(s)")
}

# ---------------------------------------------------------------------------
# 5) 集計（質問ごと・全体。TTFT はサーバ値が存在するサンプルのみ）
# ---------------------------------------------------------------------------
$perQuestion = @()
$allTtfb = @()
$allTotal = @()
$seenTags = @()
for ($qi = 0; $qi -lt $questions.Count; $qi++) {
  $tag = [string](Get-Prop $questions[$qi] "tag")
  if (-not $tag) { $tag = "q" + ($qi + 1) }
  if ($seenTags -contains $tag) { continue }
  $seenTags += $tag
  $rows = @($samples | Where-Object { $_.tag -eq $tag })
  $ttfbs = @()
  $totals = @()
  $okCount = 0
  $cachedCount = 0
  foreach ($s in $rows) {
    if ($s.ok) { $okCount++ }
    if ($s.cached -eq $true) { $cachedCount++ }
    if ($s.ok -and $null -ne $s.server_ttfb_ms) { $ttfbs += [double]$s.server_ttfb_ms }
    if ($s.ok -and $null -ne $s.server_total_ms) { $totals += [double]$s.server_total_ms }
  }
  $allTtfb += $ttfbs
  $allTotal += $totals
  $perQuestion += [pscustomobject][ordered]@{
    tag          = $tag
    samples      = $rows.Count
    ok           = $okCount
    cached       = $cachedCount
    ttfb         = Get-Stats ([double[]]$ttfbs)
    total        = Get-Stats ([double[]]$totals)
  }
}
$overall = [pscustomobject][ordered]@{
  samples = $samples.Count
  ok      = @($samples | Where-Object { $_.ok }).Count
  cached  = @($samples | Where-Object { $_.cached -eq $true }).Count
  ttfb    = Get-Stats ([double[]]$allTtfb)
  total   = Get-Stats ([double[]]$allTotal)
}

# ---------------------------------------------------------------------------
# 6) 出力（表 + JSON）
# ---------------------------------------------------------------------------
Write-Output ""
Write-Output "=== samples ==="
Write-Output ("{0,-22} {1,3} {2,-4} {3,-6} {4,-12} {5,8} {6,8} {7,8} {8,6}" -f "tag", "run", "phase", "cached", "guard", "ttfb_s", "total_s", "wall_s", "chars")
foreach ($s in $samples) {
  Write-Output ("{0,-22} {1,3} {2,-4} {3,-6} {4,-12} {5,8} {6,8} {7,8} {8,6}" -f $s.tag, $s.run, $s.phase, [string]$s.cached, [string]$s.guard, (Format-Sec $s.server_ttfb_ms), (Format-Sec $s.server_total_ms), (Format-Sec $s.client_wall_ms), $s.answer_chars)
}

Write-Output ""
Write-Output "=== summary (seconds; p50/p95 = nearest-rank) ==="
Write-Output ("{0,-22} {1,3} {2,6} {3,6} {4,6} {5,6} {6,6} {7,7} {8,7} {9,7} {10,7} {11,7}" -f "tag", "n", "ok", "ttfb50", "ttfb95", "ttfbavg", "ttfbmin", "ttfbmax", "tot50", "tot95", "totavg", "totmax")
foreach ($q in $perQuestion) {
  Write-Output ("{0,-22} {1,3} {2,6} {3,6} {4,6} {5,6} {6,6} {7,7} {8,7} {9,7} {10,7} {11,7}" -f $q.tag, $q.samples, $q.ok, (Format-Sec $q.ttfb.p50), (Format-Sec $q.ttfb.p95), (Format-Sec $q.ttfb.mean), (Format-Sec $q.ttfb.min), (Format-Sec $q.ttfb.max), (Format-Sec $q.total.p50), (Format-Sec $q.total.p95), (Format-Sec $q.total.mean), (Format-Sec $q.total.max))
}
Write-Output ("{0,-22} {1,3} {2,6} {3,6} {4,6} {5,6} {6,6} {7,7} {8,7} {9,7} {10,7} {11,7}" -f "OVERALL", $overall.samples, $overall.ok, (Format-Sec $overall.ttfb.p50), (Format-Sec $overall.ttfb.p95), (Format-Sec $overall.ttfb.mean), (Format-Sec $overall.ttfb.min), (Format-Sec $overall.ttfb.max), (Format-Sec $overall.total.p50), (Format-Sec $overall.total.p95), (Format-Sec $overall.total.mean), (Format-Sec $overall.total.max))

if (-not $OutFile) {
  $safeLabel = "perf"
  if ($Label) { $safeLabel = $Label }
  $safeLabel = $safeLabel -replace '[^\w\-.]+', '_'
  $outDir = Join-Path $scriptDir "answers"
  if (-not (Test-Path $outDir)) { [IO.Directory]::CreateDirectory($outDir) | Out-Null }
  $OutFile = Join-Path $outDir ("perf-" + $safeLabel + "-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
}
$outParent = Split-Path -Parent $OutFile
if ($outParent -and -not (Test-Path $outParent)) { [IO.Directory]::CreateDirectory($outParent) | Out-Null }

$procStats = @()
try {
  foreach ($p in (Get-Process llama-server -ErrorAction SilentlyContinue)) {
    $procStats += [pscustomobject][ordered]@{
      name           = $p.ProcessName
      pid            = $p.Id
      working_set_mb = [math]::Round($p.WorkingSet64 / 1MB, 1)
      private_mb     = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    }
  }
} catch { }

$environment = [pscustomobject][ordered]@{
  base_url       = $base
  ps_version     = $PSVersionTable.PSVersion.ToString()
  os             = [Environment]::OSVersion.VersionString
  server_version = Get-Prop $status "version"
  tier           = Get-Prop $status "tier"
  chat_model     = Get-Prop $status "chat_model"
  chunks         = Get-Prop $status "chunks"
  ram_gb         = Get-Prop $status "ram_gb"
}
$settingsInfo = $null
if ($null -ne $settings) {
  $settingsInfo = [pscustomobject][ordered]@{
    web_search          = Get-Prop $settings "web_search"
    tier                = Get-Prop $settings "tier"
    bg_friendly         = Get-Prop $settings "bg_friendly"
    idle_unload_minutes = Get-Prop $settings "idle_unload_minutes"
    drawing_enabled     = Get-Prop (Get-Prop $settings "extensions") "drawing"
  }
}

$report = [pscustomobject][ordered]@{
  schema         = "perf-suite/v1"
  label          = $Label
  generated_at   = (Get-Date).ToString("o")
  questions_file = $QuestionsFile
  runs           = $Runs
  all_cold       = [bool]$AllCold
  timeout_sec    = $TimeoutSec
  environment    = $environment
  settings       = $settingsInfo
  process_stats  = @($procStats)
  samples        = @($samples)
  summary        = [pscustomobject][ordered]@{
    per_question = @($perQuestion)
    overall      = $overall
  }
}

$json = $report | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($OutFile, $json, $utf8Bom)
Write-Output ""
Write-Output ("saved: " + $OutFile)
Write-Output ("note : outputs are UTF-8 with BOM; p50/p95 use nearest-rank; TTFT stats exclude samples without done.ttfb_ms (cached/refusal/clarify paths).")

$failed = @($samples | Where-Object { -not $_.ok }).Count
if ($failed -gt 0) {
  Write-Output ("perf-suite: completed with " + $failed + " failed sample(s)")
  exit 2
}
Write-Output "perf-suite: OK"
exit 0
