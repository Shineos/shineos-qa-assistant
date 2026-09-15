# gpt-oss-20b (MoE) E2Eベンチ: ドラフト無し vs EAGLE3ドラフト
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-server.exe'
$Main = 'D:\dev\shineos-local-ai\spikes\phase0\models\gpt-oss-20b-MXFP4.gguf'
$Draft = 'D:\dev\shineos-local-ai\spikes\phase0\models\eagle3-gpt-oss-20b-Q8_0.gguf'
$Port = 8151

# RAG相当の痩身プロンプト（Reasoning: low は gpt-ossの推論トークン抑制）
$sys = 'Reasoning: low'
$ctx = 'あなたは社内文書のみに基づくQ&Aアシスタント。回答は日本語で。結論を冒頭に。1-2文なら箇条書き無用。【文書1: 出張・旅費規程.md】出張の申請は出発日の5営業日前まで。国内出張の日当は1日あたり1,500円。タクシーは原則禁止だが22時以降の帰宅は可。宿泊費の上限は1泊15,000円。'
$q = '国内出張の日当はいくらですか'

function Run-Case([string]$label, [string[]]$extraArgs) {
  $args = @('-m', $Main, '--host', '127.0.0.1', '--port', "$Port", '-c', '4096', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8', '--jinja', '--no-repack') + $extraArgs
  $proc = Start-Process -FilePath $Engine -WindowStyle Hidden -PassThru -ArgumentList $args -RedirectStandardError "$env:TEMP\moe-$label.log"
  try {
    $ready = $false
    foreach ($i in 1..180) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
    if (-not $ready) { Write-Output "$label : NOT READY"; return }
    Write-Output "$label : ready"
    $body = @{ model = 'q'; temperature = 0.0; max_tokens = 200
               messages = @(@{ role = 'system'; content = ($sys + "`n" + $ctx) }, @{ role = 'user'; content = $q }) } | ConvertTo-Json -Depth 5
    foreach ($round in 1..3) {
      $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body)
      $t = $r.timings
      $ans = ($r.choices[0].message.content -replace "`r`n", ' ')
      Write-Output ('{0} r{1}: pp={2:N1}t/s tg={3:N1}t/s ({4}tok) total={5:N0}ms :: {6}' -f $label, $round, $t.prompt_per_second, $t.predicted_per_second, $t.predicted_n, ($t.prompt_ms + $t.predicted_ms), $ans.Substring(0, [Math]::Min(60, $ans.Length)))
    }
  } finally { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }
}

<# skip plain #>
Run-Case 'eagle3' @('--spec-draft-model', $Draft, '-c', '2048', '-ub', '256')

