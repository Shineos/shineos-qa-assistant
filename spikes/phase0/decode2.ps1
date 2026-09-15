$raw = [IO.File]::ReadAllText('D:\dev\shineos-local-ai\spikes\phase0\q.sse')
$sb = New-Object System.Text.StringBuilder
foreach ($m in [regex]::Matches($raw, '"content":\s*"((?:[^"\\]|\\.)*)"')) {
  $s = $m.Groups[1].Value
  $s = [regex]::Replace($s, '\\u([0-9a-fA-F]{4})', { param($mm) [char]::ConvertFromUtf32([Convert]::ToInt32($mm.Groups[1].Value, 16)) })
  $s = $s.Replace('\n', "`n").Replace('\"', '"')
  [void]$sb.Append($s)
}
[IO.File]::WriteAllText('D:\dev\shineos-local-ai\spikes\phase0\q.txt', $sb.ToString(), (New-Object Text.UTF8Encoding $false))
