$ErrorActionPreference = 'Stop'
$rels = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15'
foreach ($r in $rels) {
  $assets = @($r.assets | Where-Object { $_.name -match 'bin-win-(cpu-x64|vulkan-x64)' })
  '{0}  published={1}  win-assets={2}' -f $r.tag_name, $r.published_at, $assets.Count
  if ($assets.Count -gt 0 -and -not $script:found) {
    foreach ($a in $assets) {
      '   {0}  {1:N1} MB' -f $a.name, ($a.size / 1MB)
      Write-Output ('   url: ' + $a.browser_download_url)
    }
    $script:found = $true
    Write-Output '   ^^^ first release with win cpu/vulkan assets'
  }
}
