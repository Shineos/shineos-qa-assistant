# v1実スタック（Open WebUI 0.11 + Ollama qwen2.5:3b + bge-m3 + ChromaDB）ゴールデンQA
# 経路: POST /api/chat/completions (SSE) — v1フロントエンドと同一経路・RAGミドルウェア通過
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Base = 'http://127.0.0.1:8080'

$signin = @{ email = 'admin@localhost'; password = 'admin' } | ConvertTo-Json
$auth = Invoke-RestMethod -Method Post "$Base/api/v1/auths/signin" -ContentType 'application/json' -Body $utf8.GetBytes($signin)
$token = $auth.token
& curl.exe -s "$Base/api/models" -H "Authorization: Bearer $token" -o "$env:TEMP\v1-models.json"
$modelsRaw = ([IO.File]::ReadAllText("$env:TEMP\v1-models.json", $utf8)) | ConvertFrom-Json
$model = $modelsRaw.data[0].id
Write-Output ('[model] ' + $model)

$qa = @(
  @{ q = '出張時の宿泊費の上限はいくらですか';                     key = '15,000';   type = 'hit' },
  @{ q = 'パスワードはどのくらいの期間ごとに変更する必要がありますか'; key = '90日';     type = 'hit' },
  @{ q = '結婚した場合の慶弔休暇は何日ですか';                   key = '3日';      type = 'hit' },
  @{ q = '国内出張の日当はいくらですか';                         key = '1,500';    type = 'hit' },
  @{ q = 'タクシーは利用できますか';                             key = '22時';     type = 'hit' },
  @{ q = 'パスワードを忘れた場合はどうすればよいですか';           key = '1234';     type = 'hit' },
  @{ q = '海外出張の日当はいくらですか';                         key = '3,000';    type = 'hit' },
  @{ q = '年次有給休暇の申請はいつまでにすればよいですか';           key = '3日';      type = 'hit' },
  @{ q = '宇宙開発部門の予算配分について教えてください';           key = '該当';     type = 'nohit' },
  @{ q = '社内カフェテリアのメニューを教えてください';             key = '該当';     type = 'nohit' }
)

$results = @()
foreach ($item in $qa) {
  $body = @{ model = $model; stream = $true
             messages = @(@{ role = 'user'; content = $item.q }) } | ConvertTo-Json -Depth 5
  [IO.File]::WriteAllText("$env:TEMP\v1-q.json", $body, $nb)
  $t0 = Get-Date
  & curl.exe -sN --max-time 300 -o "$env:TEMP\v1-out.sse" `
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\v1-q.json" "$Base/api/chat/completions"
  $ms = [int](((Get-Date) - $t0).TotalMilliseconds)
  $raw = [IO.File]::ReadAllText("$env:TEMP\v1-out.sse", $utf8)
  $lines = @($raw -split "`n" | Where-Object { $_.Trim() })
  $content = ($lines | ForEach-Object {
    if ($_ -match '^data: (.+)$') {
      $payload = $Matches[1]
      if ($payload -eq '[DONE]') { return }
      try { $j = $payload | ConvertFrom-Json; if ($j.choices) { $j.choices[0].delta.content } } catch {}
    }
  } | Where-Object { $_ }) -join ''
  $usage = ($lines | Where-Object { $_ -match '"usage"' } | Select-Object -Last 1)
  $inTok = ''; $outTok = ''
  if ($usage -match '"input_tokens":\s*(\d+)') { $inTok = $Matches[1] }
  if ($usage -match '"output_tokens":\s*(\d+)') { $outTok = $Matches[1] }
  $hasSources = ($lines | Where-Object { $_ -match '^data: \{"sources"' }).Count -gt 0
  $flat = ($content -replace "`r`n", ' ' -replace "`n", ' ')
  $ok = $flat.Contains($item.key)
  $results += @{ q = $item.q; type = $item.type; ms = $ms; ok = $ok; inTok = $inTok; outTok = $outTok; sources = $hasSources; a = $flat }
  Write-Output ('Q: {0}  [{1}ms {2}] in={3} out={4} src={5}' -f $item.q, $ms, ($(if ($ok) { 'OK' } else { 'MISS' })), $inTok, $outTok, $hasSources)
  Write-Output ('   A: ' + $flat.Substring(0, [math]::Min(200, $flat.Length)))
}
$msVals = @($results | ForEach-Object { $_.ms })
$hitOk = @($results | Where-Object type -eq 'hit' | Where-Object ok).Count
$nohitOk = @($results | Where-Object type -eq 'nohit' | Where-Object ok).Count
$total = [int](($msVals | Measure-Object -Sum).Sum)
Write-Output ('=== v1 SUMMARY: hit {0}/8, nohit {1}/2, total {2}s, avg {3:N1}s ===' -f $hitOk, $nohitOk, [int]($total / 1000), (($msVals | Measure-Object -Average).Average / 1000))
$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 'D:\dev\shineos-local-ai\spikes\phase0\v1-results.json'
