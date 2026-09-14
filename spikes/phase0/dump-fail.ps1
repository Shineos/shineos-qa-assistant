$d = [IO.File]::ReadAllText("$env:TEMP\fail.json") | ConvertFrom-Json
$sb = New-Object System.Text.StringBuilder
foreach ($m in $d.messages) {
  [void]$sb.Append("[" + $m.role + "] " + $m.content.Substring(0, [Math]::Min(200, $m.content.Length)) + "`n")
  if ($m.sources_json) {
    $srcs = $m.sources_json | ConvertFrom-Json
    foreach ($s in $srcs) {
      if ($s.kind -ne 'web') {
        [void]$sb.Append("  SRC: " + $s.file + " snipLen=" + $s.snippet.Length + " textLen=" + $s.text.Length + "`n")
        [void]$sb.Append("    snip: " + $s.snippet.Substring(0, [Math]::Min(90, $s.snippet.Length)) + "`n")
      }
    }
  }
}
[IO.File]::WriteAllText('D:\dev\shineos-local-ai\spikes\phase0\fail.txt', $sb.ToString(), (New-Object Text.UTF8Encoding $false))
