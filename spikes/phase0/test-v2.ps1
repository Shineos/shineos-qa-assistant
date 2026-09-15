# v2アーキテクチャ（llama-server直結 + 自作RAG）ゴールデンQA — モデル指定可能
# 使い方: powershell -File test-v2.ps1 <ChatModelPath> <label> [ctx]
param(
  [string]$ChatModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf',
  [string]$Label = 'qwen3-4b',
  [int]$CtxSize = 2048
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$EmbModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-m3-Q8_0.gguf'
$RankModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf'
$KbRoot = 'C:\Program Files\ShineosQA\knowledge'   # v1が実際に取り込んだ同一コーパス
$Port = 8110
$Base = "http://127.0.0.1:$Port"
Write-Output "=== v2 test: label=$Label ctx=$CtxSize model=$ChatModel ==="

function Start-Llama([string[]]$argList, [string]$log) {
  $p = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru -ArgumentList $argList -RedirectStandardError $log
  foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "$Base/health" -TimeoutSec 2 | Out-Null; return $p } catch {} }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  throw ('llama-server not healthy: ' + ($argList -join ' '))
}
function Stop-Llama($p) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 800 } }
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

# ---- Phase A: 取り込み+埋め込み ----
$files = Get-ChildItem $KbRoot -Recurse -Include *.md | Where-Object { $_.FullName -notmatch 'アップグレード退避' }
$chunks = @()
foreach ($f in $files) {
  $raw = [IO.File]::ReadAllText($f.FullName, $utf8)
  foreach ($c in (Get-Chunks $raw)) { $chunks += @{ text = $c; file = $f.Name } }
}
Write-Output ('corpus: files={0} chunks={1}' -f $files.Count, $chunks.Count)
$p = Start-Llama @('-m', $EmbModel, '--host', '127.0.0.1', '--port', "$Port", '--embedding', '--pooling', 'cls', '-c', '2048', '-t', '8') "$env:TEMP\v2-embed.log"
try {
  $allTexts = @($chunks | ForEach-Object { $_.text }) + @($qa | ForEach-Object { $_.q })
  $j = @{ input = $allTexts } | ConvertTo-Json
  $r = Invoke-RestMethod -Method Post "$Base/v1/embeddings" -ContentType 'application/json' -Body $utf8.GetBytes($j)
  $embs = @($r.data | Sort-Object index | ForEach-Object { , [double[]]$_.embedding })
} finally { Stop-Llama $p }
$nChunks = $chunks.Count
$chunkEmbs = $embs[0..($nChunks - 1)]
$qEmbs = $embs[$nChunks..($embs.Count - 1)]

# ---- Phase B+C: 検索+リランク（検索は質問ごとに計測） ----
$p = Start-Llama @('-m', $RankModel, '--host', '127.0.0.1', '--port', "$Port", '--rerank', '--pooling', 'rank', '-c', '2048', '-t', '8') "$env:TEMP\v2-rerank.log"
$retrievedTop3 = @()
$retrMs = @()
try {
  for ($qi = 0; $qi -lt $qa.Count; $qi++) {
    $t0 = Get-Date
    $qTok = Get-Tokens $qa[$qi].q
    $scored = @()
    for ($ci = 0; $ci -lt $nChunks; $ci++) {
      $cos = Get-Cosine $qEmbs[$qi] $chunkEmbs[$ci]
      $cTok = Get-Tokens $chunks[$ci].text
      $hits = 0; foreach ($t in ($qTok | Select-Object -Unique)) { if ($cTok -contains $t) { $hits++ } }
      $scored += @{ ci = $ci; cos = $cos; kw = ($hits / [math]::Max(1, ($qTok | Select-Object -Unique).Count)) }
    }
    $cosVals = @($scored | ForEach-Object { $_.cos })
    $maxCos = ($cosVals | Measure-Object -Maximum).Maximum
    $minCos = ($cosVals | Measure-Object -Minimum).Minimum
    $top8 = $scored | ForEach-Object {
      $ncos = if ($maxCos -gt $minCos) { ($_.cos - $minCos) / ($maxCos - $minCos) } else { 0 }
      @{ ci = $_.ci; cos = $_.cos; hybrid = 0.5 * $ncos + 0.5 * $_.kw }
    } | Sort-Object hybrid -Descending | Select-Object -First 8
    $docs = @($top8 | ForEach-Object { $chunks[$_.ci].text })
    $body = @{ query = $qa[$qi].q; documents = $docs; top_n = 3 } | ConvertTo-Json
    $rr = Invoke-RestMethod -Method Post "$Base/v1/rerank" -ContentType 'application/json' -Body $utf8.GetBytes($body)
    $retrMs += [int](((Get-Date) - $t0).TotalMilliseconds)
    $ranked = @($rr.results | Sort-Object relevance_score -Descending | Select-Object -First 3)
    $retrievedTop3 += , @($ranked | ForEach-Object { @{ ci = $top8[[int]$_.index].ci; score = [double]$_.relevance_score } })
  }
} finally { Stop-Llama $p }

# ---- Phase D: チャット ----
$system = @'
あなたは社内規定・業務マニュアルに基づいて回答する社内Q&Aアシスタントです。以下のルールを守って回答してください。
1. 提供された参考文書の内容に基づいてのみ回答してください。
2. 回答の冒頭に結論を書き、その後に根拠を簡潔に説明してください。
3. 参考文書に該当する記載がない場合は、推測せず「該当する記載がありません」と回答してください。
4. 文書名を引用として示してください。
5. 一般知識による補足は行わないでください。
6. 金額・日付・回数などの数値は、文書の記載を正確に反映してください。
'@
$p = Start-Llama @('-m', $ChatModel, '--host', '127.0.0.1', '--port', "$Port", '-c', "$CtxSize", '-np', '1', '-fa', 'on', '-ub', '256', '-t', '8') "$env:TEMP\v2-chat.log"
$results = @()
try {
  for ($qi = 0; $qi -lt $qa.Count; $qi++) {
    $ctxParts = @(); $i = 0
    foreach ($t in $retrievedTop3[$qi]) { $i++; $ctxParts += ('【文書{0}: {1}】{2}' -f $i, $chunks[$t.ci].file, $chunks[$t.ci].text) }
    $ctxText = "以下は、質問の回答の参考となる社内文書の抜粋です。`n`n" + ($ctxParts -join "`n`n")
    $body = @{ model = 'q'; stream = $false; temperature = 0.0; max_tokens = 400
               messages = @(@{ role = 'system'; content = ($system + "`n" + $ctxText) }, @{ role = 'user'; content = $qa[$qi].q }) } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText("$env:TEMP\v2-q.json", $body, $nb)
    $t0 = Get-Date
    & curl.exe -s --max-time 300 -o "$env:TEMP\v2-out.json" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\v2-q.json" "$Base/v1/chat/completions"
    $wall = [int](((Get-Date) - $t0).TotalMilliseconds)
    $rj = ([IO.File]::ReadAllText("$env:TEMP\v2-out.json", $utf8)) | ConvertFrom-Json
    $content = $rj.choices[0].message.content
    $t = $rj.timings
    $flat = ($content -replace "`r`n", ' ' -replace "`n", ' ')
    $ok = if ($qa[$qi].type -eq 'nohit') { $flat.Contains('該当') } else { $flat.Contains($qa[$qi].key) }
    $results += @{ q = $qa[$qi].q; type = $qa[$qi].type; wall = $wall; retrMs = $retrMs[$qi]
                   ppTok = $t.prompt_n; ppMs = [int]$t.prompt_ms; tgTok = $t.predicted_n; tgMs = [int]$t.predicted_ms; ok = $ok; a = $flat }
    Write-Output ('Q: {0}  [wall {1}ms retr {2}ms pp {3}tok/{4}ms tg {5}tok/{6}ms {7}]' -f $qa[$qi].q, $wall, $retrMs[$qi], $t.prompt_n, [int]$t.prompt_ms, $t.predicted_n, [int]$t.predicted_ms, ($(if ($ok) { 'OK' } else { 'MISS' })))
    Write-Output ('   A: ' + $flat.Substring(0, [math]::Min(200, $flat.Length)))
  }
} finally { Stop-Llama $p }
$hitOk = @($results | Where-Object type -eq 'hit' | Where-Object ok).Count
$nohitOk = @($results | Where-Object type -eq 'nohit' | Where-Object ok).Count
$walls = @($results | ForEach-Object { $_.wall })
Write-Output ('=== v2 SUMMARY ({0}): hit {1}/8, nohit {2}/2, total {3}s, avg {4:N1}s ===' -f $Label, $hitOk, $nohitOk, [int](($walls | Measure-Object -Sum).Sum / 1000), (($walls | Measure-Object -Average).Average / 1000))
$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 ("D:\dev\shineos-local-ai\spikes\phase0\v2-results-{0}.json" -f $Label)
