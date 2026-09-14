param([string]$Label = "run")
# 品質シナリオテスト: scenarios.json の各ケースを /api/chat に流し、期待キーワード/拒否を判定。
# 出力: TSV（tag, pass, answer_chars, ms）＋ answers/<Label>-<tag>.txt
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$base = "http://127.0.0.1:8300"
& curl.exe -s -X POST "$base/api/cache-clear" | Out-Null  # answer cache is not model-tagged: always regenerate
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cases = [IO.File]::ReadAllText((Join-Path $dir "scenarios.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$ansDir = Join-Path $dir "answers"
if (-not (Test-Path $ansDir)) { [IO.Directory]::CreateDirectory($ansDir) | Out-Null }
$utf8 = New-Object Text.UTF8Encoding($false)

function Ask([string]$q, [string]$uuid) {
  $body = @{ chat_uuid = $uuid; message = $q } | ConvertTo-Json -Compress
  $tmp = [IO.Path]::GetTempFileName()
  $out = [IO.Path]::GetTempFileName()
  [IO.File]::WriteAllText($tmp, $body, $utf8)
  & curl.exe -s -N --max-time 300 -X POST "$base/api/chat" -H "Content-Type: application/json; charset=utf-8" --data-binary "@$tmp" -o $out
  $lines = [IO.File]::ReadAllLines($out, [Text.Encoding]::UTF8)
  $ev = ""; $answer = New-Object Text.StringBuilder; $done = $null
  foreach ($ln in $lines) {
    if ($ln.StartsWith("event: ")) { $ev = $ln.Substring(7).Trim() }
    elseif ($ln.StartsWith("data: ")) {
      $json = $ln.Substring(6)
      if ($ev -eq "delta") { $o = $json | ConvertFrom-Json; [void]$answer.Append($o.content) }
      elseif ($ev -eq "done") { $done = $json | ConvertFrom-Json }
    }
  }
  Remove-Item $tmp,$out -Force
  return @{ text = $answer.ToString(); ms = if ($done) { $done.ms } else { -1 } }
}

Write-Output "=== $Label ==="
$passCount = 0
foreach ($c in $cases) {
  $uuid = "scn-" + $c.tag + "-" + [Guid]::NewGuid().ToString('N').Substring(0,8)
  if ($c.PSObject.Properties["pre"] -and $c.pre) { $null = Ask $c.pre $uuid }
  $r = Ask $c.q $uuid
  $ans = $r.text
  [IO.File]::WriteAllText((Join-Path $ansDir "$Label-$($c.tag).txt"), $ans, $utf8)
  $ok = $true
  if ($c.refuse) {
    # refuse keyword comes from the JSON (read as UTF-8) - keep this file ASCII-only for PS5.1
    $kw = if ($c.PSObject.Properties["refusekw"] -and $c.refusekw) { $c.refusekw } else { $null }
    $ok = ($kw -ne $null -and $ans.Contains($kw))
  } else {
    foreach ($group in $c.expect) {
      $hit = $false
      foreach ($kw in $group) { if ($ans.Contains($kw)) { $hit = $true; break } }
      if (-not $hit) { $ok = $false; break }
    }
  }
  if ($ok) { $passCount++ }
  Write-Output ("{0}`t{1}`t{2}`t{3}" -f $c.tag, $(if ($ok) { "PASS" } else { "FAIL" }), $ans.Length, $r.ms)
}
Write-Output ("SCORE`t{0}/{1}" -f $passCount, $cases.Count)
