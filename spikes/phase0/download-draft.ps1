$ErrorActionPreference = 'Continue'
$dest = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-0.6B-Q4_K_M.gguf'
$url = 'https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q4_K_M.gguf'
& curl.exe -L -C - --retry 5 --retry-delay 3 --connect-timeout 30 -o $dest $url 2>&1 | Out-Null
$h = (Get-FileHash -Algorithm SHA256 $dest).Hash.ToLower()
if ($h -eq 'ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a') { Write-Output '[OK ] Qwen3-0.6B draft sha256 verified' } else { Write-Output ('[BAD] ' + $h) }
