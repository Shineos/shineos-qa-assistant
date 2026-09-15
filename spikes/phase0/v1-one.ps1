$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Base = 'http://127.0.0.1:8080'
$signin = @{ email = 'admin@localhost'; password = 'admin' } | ConvertTo-Json
$auth = Invoke-RestMethod -Method Post "$Base/api/v1/auths/signin" -ContentType 'application/json' -Body $utf8.GetBytes($signin)
$token = $auth.token
$models = Invoke-RestMethod -Method Get "$Base/api/models" -Headers @{ Authorization = "Bearer $token" }
$model = $models.data[0].id
[IO.File]::WriteAllText("$env:TEMP\modelid.txt", $model, $nb)
Write-Output ('model=' + $model + ' (len=' + $model.Length + ')')

$body = @{ model = $model; stream = $true; chatId = 'test'
           messages = @(@{ role = 'user'; content = '出張時の宿泊費の上限はいくらですか' }) } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText("$env:TEMP\v1-q.json", $body, $nb)
$t0 = Get-Date
& curl.exe -sN --max-time 300 -o "$env:TEMP\v1-out.sse" -w 'total=%{time_total}' `
  -H "Authorization: Bearer $token" -H 'Content-Type: application/json' --data-binary "@$env:TEMP\v1-q.json" "$Base/api/chat"
$ms = ((Get-Date) - $t0).TotalMilliseconds
Write-Output ('wall=' + [int]$ms + 'ms')
$raw = [IO.File]::ReadAllText("$env:TEMP\v1-out.sse", $utf8)
Write-Output ('--- SSE lines: ' + ($raw -split "`n").Count + ' ---')
# 先頭12行と "content" を含む行の例、sources/done を表示
($raw -split "`n" | Select-Object -First 8) | ForEach-Object { $_.Substring(0, [math]::Min(200, $_.Length)) }
Write-Output '...'
$raw -split "`n" | Where-Object { $_ -match '"type":\s*"(source|done|chat:completion)"' } | Select-Object -First 3 | ForEach-Object { $_.Substring(0, [math]::Min(400, $_.Length)) }
