$ErrorActionPreference = 'Stop'
$repos = @(
  'Qwen/Qwen3-4B-Instruct-2507-GGUF',
  'gpustack/bge-m3-GGUF',
  'gpustack/bge-reranker-v2-m3-GGUF'
)
foreach ($repo in $repos) {
  Write-Output ('=== ' + $repo + ' ===')
  $m = Invoke-RestMethod ('https://huggingface.co/api/models/' + $repo)
  foreach ($f in $m.siblings) {
    if ($f.rfilename -match '\.gguf$') {
      $size = $f.size
      $sha = $f.lfs.oid
      if (-not $size) { $size = 0 }
      '{0}  {1:N2} GB  sha256={2}' -f $f.rfilename, ($size / 1GB), $sha
    }
  }
}
