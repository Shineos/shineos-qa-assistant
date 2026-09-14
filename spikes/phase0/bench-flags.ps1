# llama-bench フラグ行列: CPU設定の最適化（pp遅の原因切り分け）
$ErrorActionPreference = 'Continue'
$bench = 'D:\dev\shineos-local-ai\spikes\phase0\engine\cpu\llama-bench.exe'
$model = 'D:\dev\shineos-local-ai\spikes\phase0\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'

$runs = @(
  @{ name = 'A_default(f16KV,no-fa)';   args = @('-fa', '0', '-t', '8') },
  @{ name = 'B_fa1_f16KV';              args = @('-fa', '1', '-t', '8') },
  @{ name = 'C_fa1_q8KV(v2想定)';        args = @('-fa', '1', '-ctk', 'q8_0', '-ctv', 'q8_0', '-t', '8') },
  @{ name = 'D_fa0_q8KV';               args = @('-fa', '0', '-ctk', 'q8_0', '-ctv', 'q8_0', '-t', '8') },
  @{ name = 'E_default_t16';            args = @('-fa', '0', '-t', '16') },
  @{ name = 'F_default_t4';             args = @('-fa', '0', '-t', '4') }
)

foreach ($r in $runs) {
  Write-Output ('=== ' + $r.name + ' ===')
  & $bench -m $model -p 512 -n 128 -r 2 @($r.args) 2>&1 | Select-String -Pattern '^\|' | ForEach-Object { $_.Line }
}
