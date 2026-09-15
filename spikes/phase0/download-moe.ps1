$ErrorActionPreference = 'Continue'
$base = 'D:\dev\shineos-local-ai\spikes\phase0\models'
$items = @(
  @{ name = 'gpt-oss-20b-MXFP4.gguf'; url = 'https://huggingface.co/ggml-org/gpt-oss-20b-GGUF/resolve/main/gpt-oss-20b-MXFP4.gguf'; sha = '' },
  @{ name = 'eagle3-gpt-oss-20b-Q8_0.gguf'; url = 'https://huggingface.co/ggml-org/gpt-oss-20b-GGUF/resolve/main/eagle3-gpt-oss-20b-Q8_0.gguf'; sha = '' }
)
foreach ($it in $items) {
  $dest = Join-Path $base $it.name
  & curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $it.url 2>&1 | Out-Null
  $sz = (Get-Item $dest -ErrorAction SilentlyContinue).Length
  Write-Output ("downloaded {0}: {1:N2} GB (exit={2})" -f $it.name, ($sz / 1GB), $LASTEXITCODE)
}
Write-Output 'MOE-DOWNLOAD-DONE'
