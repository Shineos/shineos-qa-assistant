# スモーク+ベンチ: llama-server /v1/chat/completions (Qwen3-4B-Instruct-2507 Q4_K_M)
# 計測: ロード時間 / RSS / サーバ側timings(prompt_ms=TTFT要素, predicted_per_second=生成速度) /
#       同一prefix再リクエストでのプロンプトキャッシュ効果 / SSEストリーム動作
$ErrorActionPreference = 'Stop'
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$Model  = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$Port   = 8101
$log    = "$env:TEMP\llama-chat.log"
$utf8 = [System.Text.Encoding]::UTF8
$utf8nb = New-Object Text.UTF8Encoding $false

$system = @'
あなたは社内規定・業務マニュアルに基づいて回答する社内Q&Aアシスタントです。以下のルールを守って回答してください。
1. 提供された参考文書の内容に基づいてのみ回答してください。
2. 回答の冒頭に結論を書き、その後に根拠を簡潔に説明してください。
3. 参考文書に該当する記載がない場合は、推測せず「該当する記載がありません」と回答してください。
4. 文書名や該当箇所を引用として示してください。
5. 質問の意図が不明な場合は、確認のために質問を挟んでください。
6. 一般知識による補足は行わないでください。社内規定の情報のみに基づいて回答します。
7. 金額・日付・回数などの数値は、文書の記載を正確に反映してください。勝手に推定しないでください。
8. 長文になる場合は、箇条書きや見出しを使って読みやすくしてください。
'@

$context = @'
以下は、質問の回答に参考となる社内文書の抜粋です。

【文書1: 旅費規程_QA_list.md】
Q: 宿泊費の上限はいくらですか。
A: 1泊あたり15,000円（税込）を上限とします。ただし、地域によって異なる場合があり、東京23区および政令指定都市の宿泊の場合は18,000円（税込）まで認められます。超過分は自己負担となります。宿泊先は原則として当社指定のホテル・旅館を利用してください。指定施設がない場合は、同等クラスの施設を利用し、事前に総務部へ連絡してください。

【文書2: 経費精算ガイド.md】
経費精算の流れ: (1) 領収書を取得する (2) 月末締めで経費精算書を作成する (3) 翌月10日までに所属長の承認を得る (4) 経理部へ提出する。遅延した場合は翌月支給に繰り込まれます。交通費は実費精算とし、航空券はエコノミークラスに限ります。JRは普通車・指定席まで、タクシーはやむを得ない場合に限り利用可能です。

【文書3: 情報セキュリティ規程.md】
パスワードは90日ごとに変更しなければなりません。パスワードは12文字以上で、英大文字・英小文字・数字・記号をそれぞれ1文字以上含む必要があります。3回連続で認証に失敗したアカウントは30分間ロックされます。
'@

function Get-AnswerFromStream([string]$path) {
  $raw = [IO.File]::ReadAllText($path, $utf8)
  $parts = ($raw -split "`n" | ForEach-Object {
    if ($_ -match '^data: \{(.*)\},?$') { $_.Substring(6).TrimEnd(',') }
  })
  ($parts | ForEach-Object { try { ($_ | ConvertFrom-Json).choices[0].delta.content } catch {} } | Where-Object { $_ }) -join ''
}

try {
  Write-Output '[1] starting llama-server (chat, flags: -c 8192 -np 1 -fa on -ctk q8_0 -ctv q8_0 -ub 512 -t 8)...'
  $t0 = Get-Date
  $proc = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru `
    -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '-c', '8192', '-np', '1', '-fa', 'on', '-ctk', 'q8_0', '-ctv', 'q8_0', '-ub', '512', '-t', '8') `
    -RedirectStandardError $log
  $ready = $false
  foreach ($i in 1..120) {
    Start-Sleep -Milliseconds 500
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {}
  }
  if (-not $ready) { throw 'not healthy in 60s' }
  $loadSec = ((Get-Date) - $t0).TotalSeconds
  $p = Get-Process -Id $proc.Id
  Write-Output ('[1] healthy. load={0:N1}s  RSS={1:N2} GB' -f $loadSec, ($p.WorkingSet64 / 1GB))

  $simple = @{ model = 'x'; temperature = 0.0; max_tokens = 64
               messages = @(@{ role = 'user'; content = '1+1はいくつですか？数字だけ答えてください。' }) } | ConvertTo-Json -Depth 5
  $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($simple)
  Write-Output ('[2] simple chat reply: ' + ($r.choices[0].message.content -replace "`r`n", ' '))
  if ($r.timings) { '    timings: prompt_n={0} prompt_ms={1:N0} predicted_n={2} predicted_ms={3:N0} tg={4:N1} tok/s' -f $r.timings.prompt_n, $r.timings.prompt_ms, $r.timings.predicted_n, $r.timings.predicted_ms, $r.timings.predicted_per_second }

  $q1 = '東京での出張時、宿泊費はいくらまで申請できますか？'
  $q2 = '経費精算書の提出期限はいつですか？'

  # --- req1: cold prefix, non-stream (timings = 信頼できるTTFT相当) ---
  $body1 = @{ model = 'q'; temperature = 0.0; max_tokens = 400
              messages = @(@{ role = 'system'; content = ($system + "`n" + $context) }, @{ role = 'user'; content = $q1 }) } | ConvertTo-Json -Depth 5
  $r1 = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body1)
  Write-Output '[3] req1 (cold prefix, non-stream):'
  '    answer: ' + (($r1.choices[0].message.content -replace "`r`n", ' ').Substring(0, [math]::Min(220, $r1.choices[0].message.content.Length)))
  '    timings: prompt_n={0} prompt_ms={1:N0} (pp={2:N1} tok/s)  predicted_n={3} predicted_ms={4:N0} (tg={5:N1} tok/s)' -f $r1.timings.prompt_n, $r1.timings.prompt_ms, $r1.timings.prompt_per_second, $r1.timings.predicted_n, $r1.timings.predicted_ms, $r1.timings.predicted_per_second

  # --- req2: same system+context prefix, different question ---
  $body2 = @{ model = 'q'; temperature = 0.0; max_tokens = 400
              messages = @(@{ role = 'system'; content = ($system + "`n" + $context) }, @{ role = 'user'; content = $q2 }) } | ConvertTo-Json -Depth 5
  $r2 = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body2)
  Write-Output '[4] req2 (same prefix, new question, non-stream) → prefix cache effect:'
  '    answer: ' + (($r2.choices[0].message.content -replace "`r`n", ' ').Substring(0, [math]::Min(220, $r2.choices[0].message.content.Length)))
  '    timings: prompt_n={0} prompt_ms={1:N0} (pp={2:N1} tok/s)  predicted_n={3} predicted_ms={4:N0} (tg={5:N1} tok/s)' -f $r2.timings.prompt_n, $r2.timings.prompt_ms, $r2.timings.prompt_per_second, $r2.timings.predicted_n, $r2.timings.predicted_ms, $r2.timings.predicted_per_second
  if ($r1.timings -and $r2.timings) {
    '    cache: prompt_ms {0:N0} → {1:N0} ({2:N0}% of req1)' -f $r1.timings.prompt_ms, $r2.timings.prompt_ms, (100 * $r2.timings.prompt_ms / $r1.timings.prompt_ms)
  }

  # --- req3: SSE stream correctness (UTF-8) ---
  $body3 = @{ model = 'q'; stream = $true; stream_options = @{ include_usage = $true }; temperature = 0.0; max_tokens = 300
              messages = @(@{ role = 'system'; content = ($system + "`n" + $context) }, @{ role = 'user'; content = 'パスワードの変更周期は？' }) } | ConvertTo-Json -Depth 5
  [IO.File]::WriteAllText("$env:TEMP\chat-q3.json", $body3, $utf8nb)
  $w = & curl.exe -sN -o "$env:TEMP\chat-out3.txt" -w 'wall=%{time_total}' -H 'Content-Type: application/json' --max-time 300 -d "@$env:TEMP\chat-q3.json" "http://127.0.0.1:$Port/v1/chat/completions"
  $a3 = Get-AnswerFromStream "$env:TEMP\chat-out3.txt"
  $tok3 = [regex]::Match([IO.File]::ReadAllText("$env:TEMP\chat-out3.txt", $utf8), '"completion_tokens":(\d+)').Groups[1].Value
  Write-Output ('[5] req3 (SSE stream, {0}): completion_tokens={1}' -f $w, $tok3)
  Write-Output ('    answer: ' + ($a3 -replace "`r`n", ' '))

  $p2 = Get-Process -Id $proc.Id
  Write-Output ('[6] RSS after generation: {0:N2} GB' -f ($p2.WorkingSet64 / 1GB))
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  Write-Output '[7] llama-server stopped.'
}
Write-Output '--- server log tail ---'
Get-Content $log -Tail 12
