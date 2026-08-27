# warmup_model.ps1 - PC起動時にAIモデルをメモリへ事前ロードする（v1.0.48）
# - タスクスケジューラ（ShineosWarmup・SYSTEM）から起動時に呼ばれる
# - RAM 14GB 以上の機のみ動作（2モデル同時常駐が可能なため。未満は何もしない）
# - 検索用 bge-m3 → 選択されたLLM の順にロードし、最初の質問から即答できるようにする
# 終了コード: 0 = 成功（スキップ含む）/ 1 = 失敗
param(
    [string]$Model = 'qwen2.5:3b',
    [string]$LogFile = ''
)
$ErrorActionPreference = 'Continue'
if (-not $LogFile) {
    $LogFile = Join-Path $PSScriptRoot '..\logs\warmup.log'
}
function L([string]$m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Out-File -FilePath $LogFile -Append -Encoding utf8 }

try {
    $cs = Get-CimInstance Win32_ComputerSystem
    $ramGB = [math]::Round($cs.TotalPhysicalMemory / 1GB)
    if ($ramGB -lt 14) {
        L "skip: RAM ${ramGB}GB < 14GB (single-model mode)"
        exit 0
    }

    # Ollama API の起動を待つ（最大5分）
    $deadline = (Get-Date).AddMinutes(5)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $v = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/version' -TimeoutSec 3
            if ($v.version) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) { L 'ERROR: Ollama API not ready in 5min'; exit 1 }
    L "ollama ready (RAM ${ramGB}GB) - warming models"

    # 検索用モデル（埋め込み）をロード
    try {
        $body = @{ model = 'bge-m3:latest'; input = 'warmup' } | ConvertTo-Json
        Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/embed' -Method Post `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json' -TimeoutSec 300 | Out-Null
        L 'warmed: bge-m3:latest'
    } catch { L "WARN: bge-m3 warm failed: $($_.Exception.Message)" }

    # LLM をロード（num_ctx は実運用と同じ 4096 で KV キャッシュも確保）
    try {
        $body = @{ model = $Model; prompt = 'ok'; stream = $false; keep_alive = '60m'; options = @{ num_ctx = 4096 } } | ConvertTo-Json
        Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json' -TimeoutSec 600 | Out-Null
        L "warmed: $Model"
    } catch { L "WARN: $Model warm failed: $($_.Exception.Message)" }

    L 'warmup done'
    exit 0
}
catch {
    L "ERROR: $($_.Exception.Message)"
    exit 1
}
