# Qwen3-30B-A3B (MoE, 活性3.3B) E2Eベンチ — ds4方式(mmap+--no-repack)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-server.exe'
$Main = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf'
$Port = 8152

$ctx1 = '【文書1: 出張・旅費規程.md】出張の申請は出発日の5営業日前まで。国内出張の日当は1日あたり1,500円。海外出張は1日あたり3,000円。タクシーは原則禁止だが22時以降の帰宅は可。宿泊費の上限は1泊15,000円。'
$q1 = '国内出張の日当はいくらですか'
$ctx2 = '【文書1: 慶弔休暇規程.md】結婚: 3日。配偶者の出産: 2日。忌引き(父母・配偶者): 7日。祖父母・兄弟姉妹・子: 3日。忌引きは通夜・葬儀の日を含めて連続した日数とする。'
$q2 = '忌引きで祖父母が亡くなった場合は何日ですか'

$args = @('-m', $Main, '--host', '127.0.0.1', '--port', "$Port", '-c', '2048', '-np', '1', '-fa', 'on', '-ub', '256', '-t', '8', '--jinja', '--no-repack')
$proc = Start-Process -FilePath $Engine -WindowStyle Hidden -PassThru -ArgumentList $args -RedirectStandardError "$env:TEMP\qwen-moe.log"
try {
  $ready = $false
  foreach ($i in 1..180) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
  if (-not $ready) { Write-Output 'NOT READY'; return }
  Write-Output 'ready'
  $sys = 'あなたは社内文書のみに基づくQ&Aアシスタント。回答は日本語。結論を冒頭に。推測禁止。'
  foreach ($case in @(@('c1', $ctx1, $q1), @('c2', $ctx2, $q2))) {
    $body = @{ model = 'q'; temperature = 0.0; max_tokens = 150
               messages = @(@{ role = 'system'; content = ($sys + "`n" + $case[1]) }, @{ role = 'user'; content = $case[2] }) } | ConvertTo-Json -Depth 5
    foreach ($round in 1..3) {
      $r = Invoke-RestMethod -Method Post "http://127.0.0.1:$Port/v1/chat/completions" -ContentType 'application/json' -Body $utf8.GetBytes($body)
      $t = $r.timings
      $ans = ($r.choices[0].message.content -replace "`r`n", ' ')
      Write-Output ('{0} r{1}: pp={2:N1}t/s tg={3:N1}t/s ({4}tok) total={5:N0}ms :: {6}' -f $case[0], $round, $t.prompt_per_second, $t.predicted_per_second, $t.predicted_n, ($t.prompt_ms + $t.predicted_ms), $ans.Substring(0, [Math]::Min(55, $ans.Length)))
    }
  }
} finally { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }
