$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Base = 'http://127.0.0.1:8080'
$signin = @{ email = 'admin@localhost'; password = 'admin' } | ConvertTo-Json
$auth = Invoke-RestMethod -Method Post "$Base/api/v1/auths/signin" -ContentType 'application/json' -Body $utf8.GetBytes($signin)
$token = $auth.token
# PS5.1 Invoke-RestMethod は charset 無し JSON を Latin-1 誤デコードするため curl+UTF8ファイル経由で取得
& curl.exe -s "$Base/api/models" -H "Authorization: Bearer $token" -o "$env:TEMP\v1-models.json"
$modelsRaw = ([IO.File]::ReadAllText("$env:TEMP\v1-models.json", $utf8)) | ConvertFrom-Json
$model = $modelsRaw.data[0].id

$body = @{ model = $model; stream = $true
           messages = @(@{ role = 'user'; content = '出張時の宿泊費の上限はいくらですか' }) } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText("$env:TEMP\v1-q.json", $body, $nb)
$t0 = Get-Date
& curl.exe -sN --max-time 300 -o "$env:TEMP\v1-out.sse" -w 'total=%{time_total}' `
  -H "Authorization: Bearer $token" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\v1-q.json" "$Base/api/chat/completions"
$ms = ((Get-Date) - $t0).TotalMilliseconds
Write-Output ('wall=' + [int]$ms + 'ms')
$raw = [IO.File]::ReadAllText("$env:TEMP\v1-out.sse", $utf8)
$lines = @($raw -split "`n" | Where-Object { $_.Trim() })
Write-Output ('--- SSE lines: ' + $lines.Count + ' ---')
$lines | Select-Object -First 3 | ForEach-Object { $_.Substring(0, [math]::Min(300, $_.Length)) }
Write-Output '...last...'
$lines | Select-Object -Last 3 | ForEach-Object { $_.Substring(0, [math]::Min(300, $_.Length)) }
# content 結合
$content = ($lines | ForEach-Object {
  if ($_ -match '^data: (.+)$') {
    $payload = $Matches[1]
    try { $j = $payload | ConvertFrom-Json; if ($j.choices) { $j.choices[0].delta.content } } catch {}
  }
} | Where-Object { $_ }) -join ''
Write-Output ('=== ANSWER === ' + $content)
