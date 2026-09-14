# 投機的デコーディング E2E比較（同一RAGプロンプト・llama-server・サーバ側timings）
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-server.exe'
$Main = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$Draft = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-0.6B-Q4_K_M.gguf'
$Port = 8141

$sys = 'あなたは社内文書のみに基づくQ&Aアシスタント。結論を冒頭に書くこと。回答末尾の「出典：」には、参考文書として与えられたすべての文書名を重複なく列挙すること。文書に記載がなければ「該当する記載がありません」とだけ答え、推測と一般知識は禁止。金額・日付・回数は文書どおり正確に。'
$ctx = '【文書1: 出張・旅費規程.md】出張の申請は出発日の5営業日前まで。国内出張の日当は1日あたり1,500円。タクシーは原則禁止だが22時以降の帰宅は可。宿泊費の上限は1泊15,000円。'
$q = '国内出張の日当はいくらですか'

function Run-Case([string]$label, [string[]]$extraArgs) {
  $args = @('-m', $Main, '--host', '127.0.0.1', '--port', "$Port", '-c', '2048', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8') + $extraArgs
  $proc = Start-Process -FilePath $Engine -WindowStyle Hidden -PassThru -ArgumentList $args -RedirectStandardError "$env:TEMP\spec-$label.log"
  try {
    $ready = $false
    foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
    if (-not $ready) { Write-Output "$label : NOT READY"; return }
    $body = @{ model = 'q'; temperature = 0.0; max_tokens = 200
               messages = @(@{ role = 'system'; content = ($sys + "`n" + $ctx) }, @{ role = 'user'; content = $q }) } | ConvertTo-Json -Depth 5
    # 3回計測（1回目はウォームアップ扱い）
    foreach ($round in 1..3) {
      $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body)
      $t = $r.timings
      Write-Output ('{0} r{1}: pp={2:N1}t/s tg={3:N1}t/s ({4}tok) total={5:N0}ms' -f $label, $round, $t.prompt_per_second, $t.predicted_per_second, $t.predicted_n, ($t.prompt_ms + $t.predicted_ms))
    }
  } finally { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }
}

Run-Case 'plain    ' @()
Run-Case 'spec-0.6B' @('--spec-draft-model', $Draft)
