$ErrorActionPreference = 'Stop'
$info = Invoke-RestMethod 'https://huggingface.co/api/models/Qwen/Qwen2.5-3B-Instruct-GGUF/tree/main'
foreach ($f in $info) {
  if ($f.path -match 'Q4_K_M\.gguf$') {
    '{0}  {1:N3} GB  sha256={2}' -f $f.path, ($f.size / 1GB), $f.lfs.oid
  }
}
