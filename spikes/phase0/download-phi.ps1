$ErrorActionPreference = 'Continue'
$dest = 'D:\dev\shineos-local-ai\spikes\phase0\models\Phi-4-mini-instruct-Q4_K_M.gguf'
$url = 'https://huggingface.co/lmstudio-community/Phi-4-mini-instruct-GGUF/resolve/main/Phi-4-mini-instruct-Q4_K_M.gguf'
& curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $url 2>&1 | Out-Null
$h = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLower()
if ($h -eq '3c4d3cbdf3006d81444f6c7a5a56eb93d8e0f0e2ba5963b8ab62f9fd42604233') { Write-Output '[OK ] Phi-4-mini sha256 verified' } else { Write-Output ('[BAD] ' + $h) }
