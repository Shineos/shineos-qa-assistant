$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$utf8 = [System.Text.Encoding]::UTF8
$nb = New-Object Text.UTF8Encoding $false
$Engine = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu'
$Model = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
$port = 8131
$proc = Start-Process -FilePath "$Engine\llama-server.exe" -WindowStyle Hidden -PassThru `
  -ArgumentList @('-m', $Model, '--host', '127.0.0.1', '--port', "$port", '-c', '2048', '-np', '1', '-ub', '512', '-t', '8') `
  -RedirectStandardError "$env:TEMP\dbg-chat.log"
try {
  $ready = $false
  foreach ($i in 1..120) { Start-Sleep -Milliseconds 500; try { Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 2 | Out-Null; $ready = $true; break } catch {} }
  Write-Output ('ready=' + $ready)
  $body = @{ model = 'q'; stream = $true; stream_options = @{ include_usage = $true }; temperature = 0.0; max_tokens = 50
             messages = @(@{ role = 'user'; content = '1+1は？' }) } | ConvertTo-Json -Depth 5
  $bp = "$env:TEMP\dbg-q.json"; $op = "$env:TEMP\dbg-out.txt"; $ep = "$env:TEMP\dbg-err.txt"
  [IO.File]::WriteAllText($bp, $body, $nb)
  if (Test-Path $op) { Remove-Item $op }
  $cp = Start-Process -FilePath curl.exe -ArgumentList @('-sN', '--max-time', '60', '-o', $op, '-H', 'Content-Type: application/json', '--data-binary', "@$bp", "http://127.0.0.1:$port/v1/chat/completions") -PassThru -WindowStyle Hidden -RedirectStandardError $ep
  $t0 = Get-Date
  $ttft = -1.0
  while (-not $cp.HasExited) {
    Start-Sleep -Milliseconds 25
    try {
      $fs = [IO.FileStream]::new($op, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
      $sr = New-Object IO.StreamReader($fs, $utf8); $txt = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
      if ($txt -match '"content":"..') { $ttft = ((Get-Date) - $t0).TotalSeconds; break }
    } catch {}
  }
  $cp.WaitForExit()
  Write-Output ('curl exit=' + $cp.ExitCode + ' ttft=' + [math]::Round($ttft, 2) + ' out-exists=' + (Test-Path $op))
  if (Test-Path $ep) { Write-Output ('curl stderr: ' + ([IO.File]::ReadAllText($ep, $utf8))) }
  if (Test-Path $op) {
    $raw = [IO.File]::ReadAllText($op, $utf8)
    Write-Output ('out head: ' + $raw.Substring(0, [math]::Min(200, $raw.Length)))
    Write-Output ('timings match: ' + ($raw -match '"prompt_n":\s*(\d+),\s*"prompt_ms":\s*([0-9.]+)'))
  }
} finally {
  if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
