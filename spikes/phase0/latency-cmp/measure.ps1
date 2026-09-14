param([string]$Label = "run")
# Latency measurement via the same /api/chat SSE endpoint the UI consumes.
# Prints TSV: tag, ttfb_ms, total_ms, answer_chars, cached, guard
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$base = "http://127.0.0.1:8300"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$qs = [IO.File]::ReadAllText((Join-Path $dir "questions.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$ansDir = Join-Path $dir "answers"
if (-not (Test-Path $ansDir)) { [IO.Directory]::CreateDirectory($ansDir) | Out-Null }
$utf8 = New-Object Text.UTF8Encoding($false)

function ClearCache { & curl.exe -s -X POST "$base/api/cache-clear" | Out-Null }

function Ask([string]$q, [string]$tag) {
  $uuid = [Guid]::NewGuid().ToString('N')
  $body = @{ chat_uuid = $uuid; message = $q } | ConvertTo-Json -Compress
  $tmp = [IO.Path]::GetTempFileName()
  $out = [IO.Path]::GetTempFileName()
  [IO.File]::WriteAllText($tmp, $body, $utf8)
  & curl.exe -s -N --max-time 600 -X POST "$base/api/chat" -H "Content-Type: application/json; charset=utf-8" --data-binary "@$tmp" -o $out
  if ($LASTEXITCODE -ne 0) { Write-Output "$tag`tcurl_error($LASTEXITCODE)`t-`t-`t-`t-"; Remove-Item $tmp,$out -Force; return }
  $lines = [IO.File]::ReadAllLines($out, [Text.Encoding]::UTF8)
  $ev = ""; $done = $null; $err = $null
  $answer = New-Object Text.StringBuilder
  foreach ($ln in $lines) {
    if ($ln.StartsWith("event: ")) { $ev = $ln.Substring(7).Trim() }
    elseif ($ln.StartsWith("data: ")) {
      $json = $ln.Substring(6)
      if ($ev -eq "delta") { $o = $json | ConvertFrom-Json; [void]$answer.Append($o.content) }
      elseif ($ev -eq "done") { $done = $json | ConvertFrom-Json }
      elseif ($ev -eq "error") { $err = $json }
    }
  }
  if ($err) { Write-Output "$tag`tERROR`t-`t-`t-`t-"; [IO.File]::WriteAllText((Join-Path $ansDir "$tag.err.txt"), $err, $utf8); Remove-Item $tmp,$out -Force; return }
  if (-not $done) { Write-Output "$tag`tno_done_event`t-`t-`t-`t-"; Remove-Item $tmp,$out -Force; return }
  $ttfb = if ($done.PSObject.Properties["ttfb_ms"] -and $null -ne $done.ttfb_ms) { $done.ttfb_ms } else { -1 }
  $guard = if ($done.PSObject.Properties["guard"] -and $null -ne $done.guard) { $done.guard } else { "" }
  $ans = $answer.ToString()
  [IO.File]::WriteAllText((Join-Path $ansDir "$Label-$tag.txt"), $ans, $utf8)
  Write-Output ("{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $tag, $ttfb, $done.ms, $ans.Length, $done.cached, $guard)
  Remove-Item $tmp,$out -Force
}

Write-Output "=== $Label ==="
ClearCache
Ask $qs[0].q "q1-cold"
ClearCache
Ask $qs[0].q "q1-warm"
Ask $qs[1].q "q2"
Ask $qs[2].q "q3"
Ask $qs[3].q "q4-guard"
Get-Process llama-server -ErrorAction SilentlyContinue | ForEach-Object { Write-Output ("llama-server pid={0} WS={1:N0}MB PM={2:N0}MB" -f $_.Id, ($_.WorkingSet64/1MB), ($_.PrivateMemorySize64/1MB)) }
