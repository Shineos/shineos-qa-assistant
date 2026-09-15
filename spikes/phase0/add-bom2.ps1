$p = 'D:\dev\shineos-local-ai\spikes\phase0\bench-final.ps1'
$c = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($p, $c, (New-Object Text.UTF8Encoding $true))
Write-Output 'BOM added: bench-final.ps1'
