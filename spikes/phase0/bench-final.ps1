# 追加計測: (1) Vulkan llama-server E2E動作+timings (2) CPU -c 4096 構成のRSS
$ErrorActionPreference = 'Stop'
$EngCpu = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-server.exe'
$EngVk  = 'D:\dev\shineos-local-ai\spikes\phase0\engine\vulkan\llama-server.exe'
$Model  = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$Port   = 8105
$utf8 = [System.Text.Encoding]::UTF8
$sys = 'あなたは社内規定に基づいて回答するQ&Aアシスタントです。提供文書に基づき、結論を先に、文書名を引用して答えてください。該当がなければ「該当する記載がありません」と答えます。'
$ctx = '【文書1: 出張・旅費規程.md】宿泊費の上限は1泊あたり15,000円（税込）。上限を超える場合は事前に所属長の承認が必要です。交通費は実費精算とし、航空券はエコノミークラスに限ります。【文書2: 情報セキュリティ規程.md】パスワードは12文字以上とし、90日ごとに変更します。'
function Test-Server([string]$exe, [string[]]$argList, [string]$label) {
  $log = "$env:TEMP\llama-$label.log"
  $t0 = Get-Date
  $proc = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -ArgumentList $argList -RedirectStandardError $log
  try {
    $ready = $false
    foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
    if (-not $ready) { Write-Output "$label : NOT READY"; return }
    $load = ((Get-Date) - $t0).TotalSeconds
    $rss1 = (Get-Process -Id $proc.Id).WorkingSet64 / 1GB
    $body = @{ model = 'q'; temperature = 0.0; max_tokens = 200
               messages = @(@{ role = 'system'; content = ($sys + "`n" + $ctx) },
                            @{ role = 'user'; content = '宿泊費の上限はいくらですか' }) } | ConvertTo-Json -Depth 5
    $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body)
    $rss2 = (Get-Process -Id $proc.Id).WorkingSet64 / 1GB
    Write-Output ("[{0}] load={1:N1}s RSS={2:N2}->{3:N2}GB  pp={4:N1}t/s({5}tok/{6:N0}ms) tg={7:N1}t/s({8}tok/{9:N0}ms)" -f `
      $label, $load, $rss1, $rss2, $r.timings.prompt_per_second, $r.timings.prompt_n, $r.timings.prompt_ms, `
      $r.timings.predicted_per_second, $r.timings.predicted_n, $r.timings.predicted_ms)
    Write-Output ('    answer: ' + (($r.choices[0].message.content -replace "`r`n", ' ').Substring(0, [math]::Min(150, $r.choices[0].message.content.Length))))
  } finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
  }
}
Test-Server $EngVk @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '-c', '4096', '-np', '1', '-ngl', '99', '-fa', 'off', '-t', '8') 'vulkan-c4096'
Test-Server $EngCpu @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '-c', '4096', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8') 'cpu-c4096'
Test-Server $EngCpu @('-m', $Model, '--host', '127.0.0.1', '--port', "$Port", '-c', '8192', '-np', '1', '-fa', 'on', '-ub', '512', '-t', '8') 'cpu-c8192'
