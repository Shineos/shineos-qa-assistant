$ErrorActionPreference = 'Continue'
$dest = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-1.7B-Q4_K_M.gguf'
$url = 'https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf'
& curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $url 2>&1 | Out-Null
$h = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLower()
if ($h -eq 'b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897') { Write-Output '[OK ] Qwen3-1.7B sha256 verified' } else { Write-Output ('[BAD] ' + $h) }
