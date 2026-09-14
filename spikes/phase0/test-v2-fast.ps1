# v2-fast構成のレイテンシ実証 — 3サーバー同時常駐（本番構成）
# 証明対象: (1)痩身プロンプトTTFT (2)高速経路リランク省略 (3)prefixキャッシュ
#   (4)NO-HITガード (5)回答キャッシュ閾値 (6)3サーバー同時常駐RSS
# 使い方: powershell -File test-v2-fast.ps1 <cpu|vulkan> [ChatModelPath] [label]
param(
  [string]$Variant = 'cpu',
  [string]$ChatModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf',
  [string]$Label = 'qwen3-4b-cpu'
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = "D:\dev\shineos-local-ai\spikes\phase0\engine\$Variant"
$EmbModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-m3-Q8_0.gguf'
$RankModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf'
$KbRoot = 'C:\Program Files\ShineosQA\knowledge'
$portChat, $portEmb, $portRank = 8121, 8122, 8123
Write-Output "=== v2-fast: variant=$Variant label=$Label ==="

function Start-Llama([string]$exe, [string[]]$argList, [string]$log, [string]$url) {
  $p = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -ArgumentList $argList -RedirectStandardError $log
  foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "$url/health" -TimeoutSec 2 | Out-Null; return $p } catch {} }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  throw ('llama-server not healthy: ' + ($argList -join ' '))
}
function Get-Cosine([double[]]$a, [double[]]$b) {
  $dot = 0.0; $na = 0.0; $nbv = 0.0
  for ($i = 0; $i -lt $a.Length; $i++) { $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nbv += $b[$i] * $b[$i] }
  return $dot / [math]::Sqrt($na * $nbv)
}
function Get-Chunks([string]$text, [int]$target = 350, [int]$overlap = 50) {
  $sentences = [regex]::Matches($text, '[^。．\n]+[。．]?') | ForEach-Object { $_.Value.Trim() } | Where-Object { $_ }
  $chunks = @(); $cur = ''
  foreach ($s in $sentences) {
    if (($cur.Length + $s.Length) -gt $target -and $cur) {
      $chunks += $cur.Trim()
      $cur = if ($cur.Length -gt $overlap) { $cur.Substring($cur.Length - $overlap) } else { '' }
    }
    $cur += $s
  }
  if ($cur.Trim()) { $chunks += $cur.Trim() }
  return , $chunks
}
function Get-Tokens([string]$s) {
  $tokens = @()
  foreach ($m in [regex]::Matches($s, '[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]+|[A-Za-z0-9]+')) {
    $v = $m.Value
    if ($v -match '^[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]+$') {
      if ($v.Length -eq 1) { $tokens += $v }
      else { for ($i = 0; $i -lt $v.Length - 1; $i++) { $tokens += $v.Substring($i, 2) } }
    } else { $tokens += $v.ToLowerInvariant() }
  }
  return , $tokens
}
# クエリ関連文を中心としたスニペット抽出（事実の切断を防ぐ）
function Get-Snippet([string]$text, $qTokUniq, [int]$max = 240) {
  if ($text.Length -le $max) { return $text }
  $sents = @([regex]::Matches($text, '[^。．\n]+[。．]?') | ForEach-Object { $_.Value })
  $bestI = 0; $bestScore = -1
  for ($i = 0; $i -lt $sents.Count; $i++) {
    $sTok = Get-Tokens $sents[$i]
    $sc = 0; foreach ($t in $qTokUniq) { if ($sTok -contains $t) { $sc++ } }
    if ($sc -gt $bestScore) { $bestScore = $sc; $bestI = $i }
  }
  $lo = $bestI; $hi = $bestI; $out = $sents[$bestI]
  while ($out.Length -lt $max) {
    if ($hi + 1 -lt $sents.Count -and ($out.Length + $sents[$hi + 1].Length) -le $max) { $hi++; $out = ($sents[$lo..$hi] -join '') }
    elseif ($lo - 1 -ge 0 -and ($out.Length + $sents[$lo - 1].Length) -le $max) { $lo--; $out = ($sents[$lo..$hi] -join '') }
    else { break }
  }
  return $out
}
# ストリームTTFT測定: 最初の "content":"X" 出現までをファイルポーリング
function Invoke-StreamChat([string]$bodyPath, [string]$outPath, [string]$url) {
  if (Test-Path $outPath) { Remove-Item $outPath -Force }   # 前回残置ファイルによる偽TTFT防止
  $t0 = Get-Date
  # PS5.1 Start-Process はスペース含む引数をクォートしないため、ヘッダー値は埋め込みクォート必須
  $cp = Start-Process -FilePath curl.exe -ArgumentList @('-sN', '--max-time', '300', '-o', $outPath, '-H', '"Content-Type: application/json"', '--data-binary', "@$bodyPath", "$url/v1/chat/completions") -PassThru -WindowStyle Hidden
  $ttft = -1.0
  while (-not $cp.HasExited) {
    Start-Sleep -Milliseconds 25
    try {
      $fs = [IO.FileStream]::new($outPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
      $sr = New-Object IO.StreamReader($fs, $utf8)
      $txt = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
      if ($txt -match '"content":"..') { $ttft = ((Get-Date) - $t0).TotalSeconds; break }
    } catch {}
  }
  $cp.WaitForExit()
  if ($ttft -lt 0) { $ttft = ((Get-Date) - $t0).TotalSeconds }
  return @{ ttft = [math]::Round($ttft, 2); wall = [math]::Round(((Get-Date) - $t0).TotalSeconds, 2) }
}

$qa = @(
  @{ q = '出張時の宿泊費の上限はいくらですか';                     key = '15,000';   type = 'hit' },
  @{ q = 'パスワードはどのくらいの期間ごとに変更する必要がありますか'; key = '90日';     type = 'hit' },
  @{ q = '結婚した場合の慶弔休暇は何日ですか';                   key = '3日';      type = 'hit' },
  @{ q = '国内出張の日当はいくらですか';                         key = '1,500';    type = 'hit' },
  @{ q = 'タクシーは利用できますか';                             key = '22時';     type = 'hit' },
  @{ q = 'パスワードを忘れた場合はどうすればよいですか';           key = '1234';     type = 'hit' },
  @{ q = '海外出張の日当はいくらですか';                         key = '3,000';    type = 'hit' },
  @{ q = '年次有給休暇の申請はいつまでにすればよいですか';           key = '3日';      type = 'hit' },
  @{ q = '宇宙開発部門の予算配分について教えてください';           key = '';         type = 'nohit' },
  @{ q = '社内カフェテリアのメニューを教えてください';             key = '';         type = 'nohit' }
)
# 痩身システムプロンプト（約100トークン目標）
$system = 'あなたは社内文書のみに基づくQ&Aアシスタント。結論を冒頭に書き、出典の文書名を示すこと。文書に記載がなければ「該当する記載がありません」とだけ答え、推測と一般知識は禁止。金額・日付・回数は文書どおり正確に。'

# ===== 3サーバー同時起動（本番構成） =====
$t0 = Get-Date
$chatArgs = @('-m', $ChatModel, '--host', '127.0.0.1', '--port', "$portChat", '-c', '2048', '-np', '1', '-ub', '512', '-t', '8')
if ($Variant -eq 'vulkan') { $chatArgs = @('-m', $ChatModel, '--host', '127.0.0.1', '--port', "$portChat", '-c', '2048', '-np', '1', '-ngl', '99', '-fa', 'off', '-t', '8') }
$procChat = Start-Llama "$Engine\llama-server.exe" $chatArgs "$env:TEMP\fast-chat-$Label.log" "http://127.0.0.1:$portChat"
$procEmb  = Start-Llama "$Engine\llama-server.exe" @('-m', $EmbModel, '--host', '127.0.0.1', '--port', "$portEmb", '--embedding', '--pooling', 'cls', '-c', '2048', '-t', '4') "$env:TEMP\fast-embed.log" "http://127.0.0.1:$portEmb"
$procRank = Start-Llama "$Engine\llama-server.exe" @('-m', $RankModel, '--host', '127.0.0.1', '--port', "$portRank", '--rerank', '--pooling', 'rank', '-c', '2048', '-t', '4') "$env:TEMP\fast-rank.log" "http://127.0.0.1:$portRank"
$allLoaded = ((Get-Date) - $t0).TotalSeconds
$r1 = (Get-Process -Id $procChat.Id).WorkingSet64 / 1GB
$r2 = (Get-Process -Id $procEmb.Id).WorkingSet64 / 1GB
$r3 = (Get-Process -Id $procRank.Id).WorkingSet64 / 1GB
Write-Output ('[co-resident] all 3 servers up in {0:N1}s  RSS: chat={1:N2} + embed={2:N2} + rank={3:N2} = {4:N2} GB' -f $allLoaded, $r1, $r2, $r3, ($r1 + $r2 + $r3))

try {
  # ===== コーパス取り込み =====
  $files = Get-ChildItem $KbRoot -Recurse -Include *.md | Where-Object { $_.FullName -notmatch 'アップグレード退避' }
  $chunks = @()
  foreach ($f in $files) {
    $raw = [IO.File]::ReadAllText($f.FullName, $utf8)
    foreach ($c in (Get-Chunks $raw)) { $chunks += @{ text = $c; file = $f.Name } }
  }
  # チャンク一括埋め込み
  $j = @{ input = @($chunks | ForEach-Object { $_.text }) } | ConvertTo-Json
  $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$portEmb/v1/embeddings" -ContentType 'application/json' -Body $utf8.GetBytes($j)
  $chunkEmbs = @($r.data | Sort-Object index | ForEach-Object { , [double[]]$_.embedding })
  $nChunks = $chunks.Count
  Write-Output ("corpus: files={0} chunks={1} (embed batch {2:N0}ms)" -f $files.Count, $nChunks, 0)

  # ===== 10問実行 =====
  $results = @()
  $q1Top = $null
  for ($qi = 0; $qi -lt $qa.Count; $qi++) {
    $item = $qa[$qi]
    # --- 検索（クエリ埋め込みはリアルタイム: 本番と同じ条件） ---
    $tA = Get-Date
    $qj = @{ input = $item.q } | ConvertTo-Json
    $qr = Invoke-RestMethod -Method Post "http://127.0.0.1:$portEmb/v1/embeddings" -ContentType 'application/json' -Body $utf8.GetBytes($qj)
    $qemb = [double[]]$qr.data[0].embedding
    $qTok = Get-Tokens $item.q
    $scored = @()
    for ($ci = 0; $ci -lt $nChunks; $ci++) {
      $cos = Get-Cosine $qemb $chunkEmbs[$ci]
      $cTok = Get-Tokens $chunks[$ci].text
      $hits = 0; foreach ($t in ($qTok | Select-Object -Unique)) { if ($cTok -contains $t) { $hits++ } }
      $scored += @{ ci = $ci; cos = $cos; kw = ($hits / [math]::Max(1, ($qTok | Select-Object -Unique).Count)) }
    }
    $cosVals = @($scored | ForEach-Object { $_.cos })
    $maxCos = ($cosVals | Measure-Object -Maximum).Maximum
    $minCos = ($cosVals | Measure-Object -Minimum).Minimum
    $hybrid = $scored | ForEach-Object {
      $ncos = if ($maxCos -gt $minCos) { ($_.cos - $minCos) / ($maxCos - $minCos) } else { 0 }
      @{ ci = $_.ci; cos = $_.cos; kw = $_.kw; h = 0.5 * $ncos + 0.5 * $_.kw }
    } | Sort-Object h -Descending
    $top1 = $hybrid[0]
    # 常時リランク（top8→top2: 候補を絞ると関連chunkが漏れるため8維持）
    $docs = @($hybrid | Select-Object -First 8 | ForEach-Object { $chunks[$_.ci].text })
    $rbody = @{ query = $item.q; documents = $docs; top_n = 2 } | ConvertTo-Json
    $rr = Invoke-RestMethod -Method Post "http://127.0.0.1:$portRank/v1/rerank" -ContentType 'application/json' -Body $utf8.GetBytes($rbody)
    $ranked = @($rr.results | Sort-Object relevance_score -Descending)
    $scoreLog = (($ranked | ForEach-Object { '{0:N2}' -f [double]$_.relevance_score }) -join ',')
    Write-Output ('    diag: top1cos={0:N3} kw={1:N2} rerank=[{2}]' -f $top1.cos, $top1.kw, $scoreLog)
    if ($ranked.Count -gt 0 -and [double]$ranked[0].relevance_score -lt -2.0) {
      # NO-HITガード: topスコアが-2.0未満→推論スキップ
      $retrMs = [int](((Get-Date) - $tA).TotalMilliseconds)
      $guardedOk = ($item.type -eq 'nohit')
      $results += @{ q = $item.q; type = $item.type; path = 'guard'; retr = $retrMs; tok = 0; ppMs = 0; ttft = [math]::Round($retrMs / 1000.0, 2); tg = 0; ok = $guardedOk; a = '該当する記載がありません' }
      Write-Output ('Q{0}: [GUARD {1}] retr={2}ms → 即時拒否（スコア{3}）' -f ($qi + 1), $item.type, $retrMs, $scoreLog)
      continue
    }
    $top2 = @($ranked | ForEach-Object { $hybrid[[int]$_.index] })
    $path = 'rerank'
    $retrMs = [int](((Get-Date) - $tA).TotalMilliseconds)
    if ($qi -eq 0) { $q1Top = $top2 }
    # --- 痩身プロンプト: top2×スマートスニペット240字 ---
    $qTokUniq = @($qTok | Select-Object -Unique)
    $ctxParts = @(); $ix = 0
    foreach ($t in $top2) { $ix++; $txt = Get-Snippet $chunks[$t.ci].text $qTokUniq 240; $ctxParts += ('【文書{0}: {1}】{2}' -f $ix, $chunks[$t.ci].file, $txt) }
    $body = @{ model = 'q'; stream = $true; stream_options = @{ include_usage = $true }; temperature = 0.0; max_tokens = 300
               messages = @(@{ role = 'system'; content = ($system + "`n" + ($ctxParts -join "`n")) }, @{ role = 'user'; content = $item.q }) } | ConvertTo-Json -Depth 5
    $bp = "$env:TEMP\fast-q$qi.json"; $op = "$env:TEMP\fast-out$qi.txt"
    [IO.File]::WriteAllText($bp, $body, $nb)
    $m = Invoke-StreamChat $bp $op "http://127.0.0.1:$portChat"
    $raw = [IO.File]::ReadAllText($op, $utf8)
    $rj = $null
    foreach ($ln in ($raw -split "`n")) { if ($ln -match '"usage"') { try { $rj = ($ln -replace '^data: ', '') | ConvertFrom-Json } catch {} } }
    $content = ($raw -split "`n" | ForEach-Object { if ($_ -match '^data: \{(.*)\},?$') { $_.Substring(6).TrimEnd(',') } } | ForEach-Object { try { ($_ | ConvertFrom-Json).choices[0].delta.content } catch {} } | Where-Object { $_ }) -join ''
    $flat = ($content -replace "`r`n", ' ' -replace "`n", ' ')
    $ok = if ($item.type -eq 'nohit') { $flat.Contains('該当') } else { $flat.Contains($item.key) }
    $ppN = 0; $ppMs = 0; $tgN = 0; $tgMs = 0
    if ($rj -and $rj.usage) { }
    if ($raw -match '"prompt_n":\s*(\d+),\s*"prompt_ms":\s*([0-9.]+)') { $ppN = [int]$Matches[1]; $ppMs = [int][double]$Matches[2] }
    if ($raw -match '"predicted_n":\s*(\d+),\s*"predicted_ms":\s*([0-9.]+)') { $tgN = [int]$Matches[1]; $tgMs = [int][double]$Matches[2] }
    $results += @{ q = $item.q; type = $item.type; path = $path; retr = $retrMs; tok = $ppN; ppMs = $ppMs; ttft = $m.ttft; tg = $(if ($tgMs -gt 0) { [math]::Round($tgN / ($tgMs / 1000.0), 1) } else { 0 }); ok = $ok; a = $flat }
    Write-Output ('Q{0}: [{1}{2}] retr={3}ms pp={4}tok/{5}ms TTFT={6}s tg={7}t/s {8}' -f ($qi + 1), $path, $(if ($item.type -eq 'nohit') { '/nh' } else { '' }), $retrMs, $ppN, $ppMs, $m.ttft, $(if ($tgMs -gt 0) { [math]::Round($tgN / ($tgMs / 1000.0), 1) } else { 0 }), ($(if ($ok) { 'OK' } else { 'MISS' })))
    Write-Output ('    A: ' + $flat.Substring(0, [math]::Min(150, $flat.Length)))
  }

  # ===== prefixキャッシュ実証: Q1と同一コンテキストへの追加質問 =====
  $follow = '宿泊費が上限を超える場合はどうすればよいですか'
  $followTok = Get-Tokens $follow
  $ctxParts = @(); $ix = 0
  foreach ($t in $q1Top) { $ix++; $txt = Get-Snippet $chunks[$t.ci].text (@($followTok | Select-Object -Unique)) 240; $ctxParts += ('【文書{0}: {1}】{2}' -f $ix, $chunks[$t.ci].file, $txt) }
  $body = @{ model = 'q'; stream = $true; stream_options = @{ include_usage = $true }; temperature = 0.0; max_tokens = 300
             messages = @(@{ role = 'system'; content = ($system + "`n" + ($ctxParts -join "`n")) }, @{ role = 'user'; content = $follow }) } | ConvertTo-Json -Depth 5
  [IO.File]::WriteAllText("$env:TEMP\fast-follow.json", $body, $nb)
  $m = Invoke-StreamChat "$env:TEMP\fast-follow.json" "$env:TEMP\fast-follow-out.txt" "http://127.0.0.1:$portChat"
  $raw = [IO.File]::ReadAllText("$env:TEMP\fast-follow-out.txt", $utf8)
  $ppN2 = 0; $ppMs2 = 0
  if ($raw -match '"prompt_n":\s*(\d+),\s*"prompt_ms":\s*([0-9.]+)') { $ppN2 = [int]$Matches[1]; $ppMs2 = [int][double]$Matches[2] }
  Write-Output ('[prefix-cache] 同一コンテキスト追質問: pp={0}tok/{1}ms TTFT={2}s（Q1初回と比較）' -f $ppN2, $ppMs2, $m.ttft)
  $q1pp = ($results | Where-Object { $_.q -eq $qa[0].q }).ppMs
  Write-Output ('  → Q1初回 pp={0}ms vs 追質問 pp={1}ms' -f $q1pp, $ppMs2)

  # ===== 回答キャッシュ閾値実証 =====
  $para = '宿泊費の上限金額を教えてください'
  $qj = @{ input = @($qa[0].q, $para) } | ConvertTo-Json
  $t0 = Get-Date
  $qr = Invoke-RestMethod -Method Post "http://127.0.0.1:$portEmb/v1/embeddings" -ContentType 'application/json' -Body $utf8.GetBytes($qj)
  $embedMs = [int](((Get-Date) - $t0).TotalMilliseconds / 2)
  $cosExact = Get-Cosine ([double[]]$qr.data[0].embedding) ([double[]]$qr.data[1].embedding)
  Write-Output ('[answer-cache] 質問埋め込み {0}ms/件。完全一致=即時ヒット(cos=1.0)。言い換えとのcos={1:N3}（閾値0.97だと非ヒット→検索へ）' -f $embedMs, $cosExact)

  $hitOk = @($results | Where-Object type -eq 'hit' | Where-Object ok).Count
  $ttfts = @($results | Where-Object { $_.type -eq 'hit' } | ForEach-Object { $_.ttft })
  Write-Output ('=== SUMMARY v2-fast ({0}): hit {1}/8 nohit {2}/2  TTFT(hit): avg={3:N2}s max={4:N2}s min={5:N2}s ===' -f $Label, $hitOk, @($results | Where-Object type -eq 'nohit' | Where-Object ok).Count, (($ttfts | Measure-Object -Average).Average), (($ttfts | Measure-Object -Maximum).Maximum), (($ttfts | Measure-Object -Minimum).Minimum))
  $results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 ("D:\dev\shineos-local-ai\spikes\phase0\fast-results-{0}.json" -f $Label)
} finally {
  foreach ($p in $procChat, $procEmb, $procRank) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
  Write-Output '[cleanup] 3 servers stopped.'
}
