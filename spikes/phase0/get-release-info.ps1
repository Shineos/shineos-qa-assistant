$ErrorActionPreference = 'Stop'
$r = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest'
Write-Output ('tag: ' + $r.tag_name)
foreach ($a in $r.assets) {
  if ($a.name -match 'win-(cpu|vulkan|avx2)-x64') {
    '{0}  {1:N1} MB' -f $a.name, ($a.size / 1MB)
    Write-Output ('  url: ' + $a.browser_download_url)
  }
}
