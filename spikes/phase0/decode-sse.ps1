$raw = [IO.File]::ReadAllText('D:\dev\shineos-local-ai\spikes\phase0\q.sse')
$sb = New-Object System.Text.StringBuilder
foreach ($line in ($raw -split "`n")) {
  if ($line.StartsWith('data: {')) {
    $json = $line.Substring(6).TrimEnd(',').Trim()
    try {
      $o = $json | ConvertFrom-Json
      if ($o.choices -and $o.choices.Count -gt 0 -and $o.choices[0].delta -and $o.choices[0].delta.content) {
        [void]$sb.Append($o.choices[0].delta.content)
      }
    } catch {}
  }
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Output $sb.ToString()
