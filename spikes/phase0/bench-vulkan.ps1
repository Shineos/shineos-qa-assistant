$ErrorActionPreference = 'Continue'
$bench = 'D:\dev\shineos-local-ai\spikes\phase0\engine\vulkan\llama-bench.exe'
$model = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
Write-Output '=== Vulkan (AMD Radeon iGPU, -ngl 99 -fa 1) ==='
& $bench -m $model -p 512 -n 128 -r 2 -fa 1 -ngl 99 2>&1 | Select-String -Pattern '^\|' | ForEach-Object { $_.Line }
Write-Output '=== Vulkan (fa off) ==='
& $bench -m $model -p 512 -n 128 -r 2 -fa 0 -ngl 99 2>&1 | Select-String -Pattern '^\|' | ForEach-Object { $_.Line }
