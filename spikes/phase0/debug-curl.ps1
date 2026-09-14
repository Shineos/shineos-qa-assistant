$op = "$env:TEMP\dbg-out.txt"
if (Test-Path $op) { Remove-Item $op }
$cp = Start-Process -FilePath curl.exe -ArgumentList @('-sN', '--max-time', '10', '-o', $op, 'https://example.com') -PassThru -WindowStyle Hidden
$cp.WaitForExit()
Write-Output ('exit=' + $cp.ExitCode + ' file-exists=' + (Test-Path $op))
$cp2 = Start-Process -FilePath curl.exe -ArgumentList @('-sN', '--max-time', '10', '-o', $op, '-H', 'Content-Type: application/json', '--data-binary', '@C:\nonexistent.json', 'http://127.0.0.1:1/v1/x') -PassThru -WindowStyle Hidden
$cp2.WaitForExit()
Write-Output ('exit2=' + $cp2.ExitCode + ' file-exists2=' + (Test-Path $op))
