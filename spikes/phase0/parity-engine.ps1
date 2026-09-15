# エンジンパリティ計測: Ollama(qwen2.5:3b) vs llama-server(qwen2.5-3b Q4_K_M GGUF)
# 同一チャットプロンプト（日本語RAG相当・非ストリーム）でpp/tg速度を比較
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-server.exe'
$Model = 'D:\dev\shineos-local-ai\spikes\phase0\models\qwen2.5-3b-instruct-q4_k_m.gguf'
$Port = 8107

$system = 'あなたは社内規定に基づいて回答するQ&Aアシスタントです。提供された参考文書のみに基づき、結論を冒頭に、文書名を引用して回答します。該当する記載がない場合は推測せず「該当する記載がありません」と答えます。金額・日付・回数は文書の記載を正確に反映します。'
$ctx = '【文書1: 出張・旅費規程.md】出張の申請は出発日の5営業日前までに出張申請システムで行う。承認は所属長。1週間を超える出張は部長の承認が必要。国内出張の日当は1日あたり1,500円、海外出張は1日あたり3,000円。日当は出張日数分（移動日を含む）を支給する。電車・バスは実費支給。タクシーは原則禁止だが、22時以降に帰宅する場合は利用できる。マイカー利用は事前申請が必要（1kmあたり20円）。宿泊費の上限は1泊あたり15,000円（税込）。上限を超える場合は事前に所属長の承認が必要。'
$q = '出張時の宿泊費の上限と、タクシーを利用できる条件を教えてください。'
$messages = @(@{ role = 'system'; content = ($system + "`n" + $ctx) }, @{ role = 'user'; content = $q })

# --- Ollama ---
$ollamaBody = @{ model = 'qwen2.5:3b'; stream = $false; options = @{ temperature = 0; num_predict = 200; num_ctx = 4096 }
                 messages = $messages } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText("$env:TEMP\par-ollama.json", $ollamaBody, $nb)
$t0 = Get-Date
& curl.exe -s --max-time 300 -o "$env:TEMP\par-ollama-out.json" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\par-ollama.json" http://127.0.0.1:11434/api/chat
$ollamaWall = [int](((Get-Date) - $t0).TotalMilliseconds)
$oj = ([IO.File]::ReadAllText("$env:TEMP\par-ollama-out.json", $utf8)) | ConvertFrom-Json
$ppN = $oj.prompt_eval_count; $ppD = $oj.prompt_eval_duration / 1000000.0
$tgN = $oj.eval_count; $tgD = $oj.eval_duration / 1000000.0
Write-Output ('[Ollama    ] wall={0}ms  pp: {1}tok/{2:N0}ms={3:N1}t/s  tg: {4}tok/{5:N0}ms={6:N1}t/s' -f $ollamaWall, $ppN, $ppD, ($ppN / $ppD * 1000), $tgN, $tgD, ($tgN / $tgD * 1000))
Write-Output ('  A: ' + (($oj.message.content -replace "`r`n", ' ').Substring(0, [math]::Min(150, $oj.message.content.Length))))

# --- llama-server ---
$proc = Start-Process -FilePath $Engine -WindowStyle Hidden -PassThru `
  -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '-c', '4096', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8') `
  -RedirectStandardError "$env:TEMP\par-llama.log"
try {
  $ready = $false
  foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
  if (-not $ready) { throw 'llama-server not healthy' }
  $llamaBody = @{ model = 'q'; temperature = 0.0; max_tokens = 200; messages = $messages } | ConvertTo-Json -Depth 6
  [IO.File]::WriteAllText("$env:TEMP\par-llama.json", $llamaBody, $nb)
  $t0 = Get-Date
  & curl.exe -s --max-time 300 -o "$env:TEMP\par-llama-out.json" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\par-llama.json" "http://127.0.0.1:$Port/v1/chat/completions"
  $llamaWall = [int](((Get-Date) - $t0).TotalMilliseconds)
  $lj = ([IO.File]::ReadAllText("$env:TEMP\par-llama-out.json", $utf8)) | ConvertFrom-Json
  $t = $lj.timings
  Write-Output ('[llama-srv ] wall={0}ms  pp: {1}tok/{2:N0}ms={3:N1}t/s  tg: {4}tok/{5:N0}ms={6:N1}t/s' -f $llamaWall, $t.prompt_n, $t.prompt_ms, $t.prompt_per_second, $t.predicted_n, $t.predicted_ms, $t.predicted_per_second)
  Write-Output ('  A: ' + (($lj.choices[0].message.content -replace "`r`n", ' ').Substring(0, [math]::Min(150, $lj.choices[0].message.content.Length))))
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
