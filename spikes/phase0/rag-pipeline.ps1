# RAGミニパイプライン検証（実ナレッジデータ）
# 流れ: Phase A(.md取り込み→チャンク→チャンク+質問を埋め込み) → Phase B(ハイブリッドtop8)
#      → Phase C(リランクtop3) → Phase D(ガードレールプロンプト→Qwen3-4B出典付き回答)
$ErrorActionPreference = 'Stop'
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$ChatModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$EmbModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-m3-Q8_0.gguf'
$RankModel = 'D:\dev\shineos-local-ai\spikes\phase0\models\bge-reranker-v2-m3-Q8_0.gguf'
$KbRoot = 'D:\dev\shineos-local-ai\spikes\phase0\testdocs\knowledge-sample'
$Port = 8100
$Base = "http://127.0.0.1:$Port"
$utf8 = [System.Text.Encoding]::UTF8

function Start-Llama([string[]]$argList, [string]$log) {
  $p = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru -ArgumentList $argList -RedirectStandardError $log
  foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "$Base/health" -TimeoutSec 2 | Out-Null; return $p } catch {} }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  throw ('llama-server not healthy: ' + ($argList -join ' '))
}
function Stop-Llama($p) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 500 } }
function Get-Cosine([double[]]$a, [double[]]$b) {
  $dot = 0.0; $na = 0.0; $nb = 0.0
  for ($i = 0; $i -lt $a.Length; $i++) { $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nb += $b[$i] * $b[$i] }
  return $dot / [math]::Sqrt($na * $nb)
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

$questions = @(
  @{ q = '宿泊費の上限はいくらですか'; expect = '旅費/QA' },
  @{ q = 'パスワードはどのくらいの期間で変更する必要がありますか'; expect = 'セキュリティ' },
  @{ q = '結婚した場合の休暇は何日ですか'; expect = '慶弔' },
  @{ q = '宇宙開発部門の予算配分について教えてください'; expect = 'NO-HIT' }
)

# ============ Phase A: 取り込み + 埋め込み（チャンク＋質問） ============
Write-Output '=== Phase A: ingest + embed ==='
$files = Get-ChildItem $KbRoot -Recurse -Include *.md
$chunks = @()
foreach ($f in $files) {
  $raw = [IO.File]::ReadAllText($f.FullName, $utf8)
  foreach ($c in (Get-Chunks $raw)) { $chunks += @{ text = $c; file = $f.Name } }
}
Write-Output ('files={0} chunks={1}' -f $files.Count, $chunks.Count)

$p = Start-Llama @('-m', $EmbModel, '--host', '127.0.0.1', '--port', "$Port", '--embedding', '--pooling', 'cls', '-c', '2048', '-t', '8') "$env:TEMP\rag-embed.log"
try {
  $allTexts = @($chunks | ForEach-Object { $_.text }) + @($questions | ForEach-Object { $_.q })
  $j = @{ input = $allTexts } | ConvertTo-Json
  $t0 = Get-Date
  $r = Invoke-RestMethod -Method Post "$Base/v1/embeddings" -ContentType 'application/json' -Body $utf8.GetBytes($j)
  $embMs = ((Get-Date) - $t0).TotalMilliseconds
  Write-Output ('embedded {0} texts in {1:N0} ms ({2:N1} ms/text)' -f $allTexts.Count, $embMs, ($embMs / $allTexts.Count))
  $embs = @($r.data | Sort-Object index | ForEach-Object { , [double[]]$_.embedding })
} finally { Stop-Llama $p }
$nChunks = $chunks.Count
$chunkEmbs = $embs[0..($nChunks - 1)]
$qEmbs = $embs[$nChunks..($embs.Count - 1)]

# ============ Phase B: ハイブリッド検索 (top8) ============
Write-Output '=== Phase B: hybrid retrieval top8 (vec 0.5 + keyword 0.5) ==='
$tB = Get-Date
$retrieved = @()
for ($qi = 0; $qi -lt $questions.Count; $qi++) {
  $q = $questions[$qi].q
  $qTok = Get-Tokens $q
  $scored = @()
  for ($ci = 0; $ci -lt $nChunks; $ci++) {
    $cos = Get-Cosine $qEmbs[$qi] $chunkEmbs[$ci]
    $cTok = Get-Tokens $chunks[$ci].text
    $hits = 0; foreach ($t in ($qTok | Select-Object -Unique)) { if ($cTok -contains $t) { $hits++ } }
    $kw = $hits / [math]::Max(1, ($qTok | Select-Object -Unique).Count)
    $scored += @{ ci = $ci; cos = $cos; kw = $kw }
  }
  $cosVals = @($scored | ForEach-Object { $_.cos })
  $maxCos = ($cosVals | Measure-Object -Maximum).Maximum
  $minCos = ($cosVals | Measure-Object -Minimum).Minimum
  $top8 = $scored | ForEach-Object {
    $ncos = if ($maxCos -gt $minCos) { ($_.cos - $minCos) / ($maxCos - $minCos) } else { 0 }
    @{ ci = $_.ci; cos = $_.cos; kw = $_.kw; hybrid = 0.5 * $ncos + 0.5 * $_.kw }
  } | Sort-Object hybrid -Descending | Select-Object -First 8
  Write-Output ('Q{0}: {1}' -f ($qi + 1), $q)
  foreach ($t in ($top8 | Select-Object -First 3)) {
    '    cos={0:N3} kw={1:N2} h={2:N3}  [{3}] {4}' -f $t.cos, $t.kw, $t.hybrid, $chunks[$t.ci].file, ($chunks[$t.ci].text.Substring(0, [math]::Min(40, $chunks[$t.ci].text.Length)))
  }
  $retrieved += , @($top8)
}
Write-Output ('Phase B took {0:N0} ms for {1} queries × {2} chunks (pure PS, 本番はC#/FTS5)' -f ((Get-Date) - $tB).TotalMilliseconds, $questions.Count, $nChunks)

# ============ Phase C: リランク top8 → top3 ============
Write-Output '=== Phase C: rerank top8 -> top3 ==='
$p = Start-Llama @('-m', $RankModel, '--host', '127.0.0.1', '--port', "$Port", '--rerank', '--pooling', 'rank', '-c', '4096', '-t', '8') "$env:TEMP\rag-rerank.log"
$finalTop = @()
try {
  for ($qi = 0; $qi -lt $questions.Count; $qi++) {
    $docs = @($retrieved[$qi] | ForEach-Object { $chunks[$_.ci].text })
    $body = @{ query = $questions[$qi].q; documents = $docs; top_n = 3 } | ConvertTo-Json
    $t0 = Get-Date
    $r = Invoke-RestMethod -Method Post "$Base/v1/rerank" -ContentType 'application/json' -Body $utf8.GetBytes($body)
    $ms = ((Get-Date) - $t0).TotalMilliseconds
    $ranked = $r.results | Sort-Object relevance_score -Descending
    Write-Output ('Q{0}: rerank {1}docs {2:N0}ms  top3: {3}' -f ($qi + 1), $docs.Count, $ms, (($ranked | ForEach-Object { '{0}({1:N2})' -f $retrieved[$qi][[int]$_.index].ci, [double]$_.relevance_score }) -join ' '))
    $finalTop += , @($ranked | ForEach-Object { $retrieved[$qi][[int]$_.index] })
  }
} finally { Stop-Llama $p }

# ============ Phase D: ガードレール＋出典付き回答 ============
Write-Output '=== Phase D: chat with citations (Qwen3-4B) ==='
$system = @'
あなたは社内規定・業務マニュアルに基づいて回答する社内Q&Aアシスタントです。以下のルールを守って回答してください。
1. 提供された参考文書の内容に基づいてのみ回答してください。
2. 回答の冒頭に結論を書き、その後に根拠を簡潔に説明してください。
3. 参考文書に該当する記載がない場合は、推測せず「該当する記載がありません」と回答してください。
4. 文書名を引用として示してください。
5. 一般知識による補足は行わないでください。
6. 金額・日付・回数などの数値は、文書の記載を正確に反映してください。
'@
$p = Start-Llama @('-m', $ChatModel, '--host', '127.0.0.1', '--port', "$Port", '-c', '8192', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8') "$env:TEMP\rag-chat.log"
try {
  for ($qi = 0; $qi -lt $questions.Count; $qi++) {
    $ctxParts = @()
    $i = 0
    foreach ($t in $finalTop[$qi]) {
      $i++
      $ctxParts += ('【文書{0}: {1}】{2}' -f $i, $chunks[$t.ci].file, $chunks[$t.ci].text)
    }
    $ctx = "以下は、質問の回答の参考となる社内文書の抜粋です。`n`n" + ($ctxParts -join "`n`n")
    $body = @{ model = 'q'; stream = $true; temperature = 0.0; max_tokens = 400
               messages = @(
                 @{ role = 'system'; content = ($system + "`n" + $ctx) },
                 @{ role = 'user'; content = $questions[$qi].q }) } | ConvertTo-Json -Depth 5
    $bp = "$env:TEMP\rag-q$qi.json"; $op = "$env:TEMP\rag-out$qi.txt"
    [IO.File]::WriteAllText($bp, $body, (New-Object Text.UTF8Encoding $false))
    $w = & curl.exe -sN -o $op -w 'ttft=%{time_starttransfer} total=%{time_total}' -H 'Content-Type: application/json' --max-time 300 -d "@$bp" "$Base/v1/chat/completions"
    $raw = [IO.File]::ReadAllText($op, $utf8)
    $tok = [regex]::Match($raw, '"completion_tokens":(\d+)').Groups[1].Value
    $answer = ($raw -split "`n" | ForEach-Object { if ($_ -match '^data: \{(.*)\}$') { $_.Substring(6) } } | ForEach-Object { try { ($_ | ConvertFrom-Json).choices[0].delta.content } catch {} } | Where-Object { $_ }) -join ''
    Write-Output ('--- Q{0}: {1}  [{2}]  {3} tokens, {4}' -f ($qi + 1), $questions[$qi].q, $questions[$qi].expect, $tok, $w)
    Write-Output ('    ' + ($answer -replace "`r`n", "`n" -replace "`n", ' '))
  }
} finally { Stop-Llama $p }
Write-Output '=== DONE ==='
