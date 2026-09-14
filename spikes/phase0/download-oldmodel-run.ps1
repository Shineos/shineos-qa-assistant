$ErrorActionPreference = 'Continue'
$dest = 'D:\dev\shineos-local-ai\spikes\phase0\models\qwen2.5-3b-instruct-q4_k_m.gguf'
$url = 'https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf'
& curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $url 2>&1 | Out-Null
Write-Output ('curl exit=' + $LASTEXITCODE)
$h = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLower()
$expect = '626b4a6678b86442240e33df819e00132d3ba7dddfe1cdc4fbb18e0a9615c62d'
if ($h -eq $expect) { Write-Output '[OK ] qwen2.5-3b sha256 verified' } else { Write-Output ('[BAD] ' + $h) }
