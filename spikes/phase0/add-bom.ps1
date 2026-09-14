$ErrorActionPreference = 'Stop'
foreach ($f in @('smoke-rerank.ps1', 'smoke-chat.ps1', 'smoke-embed.ps1')) {
  $p = 'D:\dev\shineos-local-ai\spikes\phase0\' + $f
  $c = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
  [IO.File]::WriteAllText($p, $c, (New-Object Text.UTF8Encoding $true))
  Write-Output ('BOM added: ' + $f)
}
