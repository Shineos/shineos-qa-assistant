$ErrorActionPreference = 'Continue'
$root = 'D:\dev\shineos-local-ai\spikes\phase0\models'
New-Item -ItemType Directory -Force -Path $root | Out-Null

$models = @(
  @{ name = 'bge-m3-Q8_0.gguf';         url = 'https://huggingface.co/gpustack/bge-m3-GGUF/resolve/main/bge-m3-Q8_0.gguf';         sha = '950f4a8e5e19477a6d3c26d2f162233c20002c601f75e4b002e3239997821167' },
  @{ name = 'bge-reranker-v2-m3-Q8_0.gguf'; url = 'https://huggingface.co/gpustack/bge-reranker-v2-m3-GGUF/resolve/main/bge-reranker-v2-m3-Q8_0.gguf'; sha = 'a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3' },
  @{ name = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'; url = 'https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'; sha = '3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597' }
)

foreach ($m in $models) {
  $dest = Join-Path $root $m.name
  Write-Output ("[DL ] " + $m.name)
  & curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $m.url 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Output ("[ERR] curl exit=" + $LASTEXITCODE + " " + $m.name); continue }
  Write-Output ("[HASH] " + $m.name)
  $h = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLower()
  if ($h -eq $m.sha) {
    Write-Output ("[OK ] " + $m.name + " sha256 verified")
  } else {
    Write-Output ("[BAD] " + $m.name + " expected=" + $m.sha + " actual=" + $h)
  }
}
Write-Output '[DONE] all downloads processed'
