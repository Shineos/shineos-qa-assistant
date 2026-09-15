$ErrorActionPreference = 'Continue'
$dest = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf'
$url = 'https://huggingface.co/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF/resolve/main/Qwen3-30B-A3B-Instruct-2507-UD-Q3_K_XL.gguf'
& curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $url 2>&1 | Out-Null
$sz = (Get-Item $dest -ErrorAction SilentlyContinue).Length
Write-Output ("downloaded Qwen3-30B-A3B UD-Q3_K_XL: {0:N2} GB (exit={1})" -f ($sz / 1GB), $LASTEXITCODE)
Write-Output 'QWEN-MOE-DONE'
