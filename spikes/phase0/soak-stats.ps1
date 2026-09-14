$rows = Import-Csv 'D:\dev\shineos-local-ai\spikes\phase0\soak.csv'
$stats = @{}
foreach ($r in $rows) {
  if (-not $stats[$r.cls]) { $stats[$r.cls] = @{ walls = New-Object System.Collections.Generic.List[int]; ok = 0; n = 0 } }
  $s = $stats[$r.cls]
  $s.n++
  if ($r.ok -eq 'OK') { $s.ok++ }
  if ($r.cls -ne 'cache') { [void]$s.walls.Add([int]$r.wall_ms) }
}
$out = New-Object System.Text.StringBuilder
foreach ($k in @('hit','guard','nohit','cache')) {
  if (-not $stats[$k]) { continue }
  $s = $stats[$k]
  $w = $s.walls | Sort-Object
  if ($w.Count -gt 0) {
    $avg = [math]::Round(($w | Measure-Object -Average).Average)
    $med = $w[[int][math]::Floor($w.Count / 2)]
    $p90 = $w[[int][math]::Floor($w.Count * 0.9)]; if ($p90 -ge $w.Count) { $p90 = $w.Count - 1 }
    [void]$out.Append(("$k : n=$($s.n) ok=$($s.ok)/$($s.n) avg=${avg}ms med=${med}ms min=$($w[0])ms max=$($w[-1])ms p90=$($p90)ms`n"))
  } else {
    [void]$out.Append(("$k : n=$($s.n) ok=$($s.ok)/$($s.n)`n"))
  }
}
[IO.File]::WriteAllText('D:\dev\shineos-local-ai\spikes\phase0\soak-stats.txt', $out.ToString(), (New-Object Text.UTF8Encoding $false))
