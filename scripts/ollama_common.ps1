# ollama_common.ps1 - ハードウェア検出の共通関数
# setup_ollama.ps1 / configure_model.ps1 から dot-source して使う。
# このファイルは副作用を持たないこと（関数定義のみ。環境変数・サービス・ファイルに触らない）。

# GPU 加速の見込みを検出する。
#   戻り値:
#     'cuda'    : NVIDIA GPU を検出 → Ollama が自動で CUDA オフロードする
#     'amddgpu' : AMD デスクトップGPU（専用VRAM 2GB以上）→ 対応カードなら Ollama が
#                 ROCm でオフロードする（対応外カードは自動で CPU にフォールバック）
#     'cpu'     : 内蔵GPUのみ / 判定不能 → CPU 推論
# 実際のオフロード可否（VRAM 容量・カードの対応有無）は Ollama 自身が起動時に
# 自動判定する。本検出は「チューニング方針の決定とインストールログへの記録」が目的で、
# 我々のスクリプトが GPU の使用を強制・無効化することはない。
function Get-GpuAcceleration {
    $devices = @()
    # VRAM 容量はレジストリの qwMemorySize（QWORD）を優先して読む。
    # Win32_VideoController.AdapterRAM は 32bit 値のため 4GB を超えるVRAMを正しく報告しない
    try {
        $base = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
        $keys = @(Get-ChildItem $base -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^\d+$' })
        foreach ($k in $keys) {
            $p = Get-ItemProperty -Path $k.PSPath -ErrorAction SilentlyContinue
            if (-not $p -or -not $p.DriverDesc) { continue }
            $vram = 0.0
            $q = $p.'HardwareInformation.qwMemorySize'
            if ($q) { $vram = [math]::Round($q / 1GB, 1) }
            else {
                $m = $p.'HardwareInformation.MemorySize'
                if ($m) { $vram = [math]::Round(([byte[]]@($m))[0] / 1GB, 1) }
            }
            $devices += [pscustomobject]@{ Name = [string]$p.DriverDesc; VramGB = $vram }
        }
    } catch { }
    if (-not $devices) {
        try {
            $devices = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object {
                $v = 0.0
                if ($_.AdapterRAM) { $v = [math]::Round($_.AdapterRAM / 1GB, 1) }
                [pscustomobject]@{ Name = [string]$_.Name; VramGB = $v }
            })
        } catch { return 'cpu' }
    }
    # NVIDIA は専用VRAM前提のため名前だけで判定（VRAM容量は Ollama が自動判断する）
    foreach ($d in $devices) {
        if ($d.Name -match 'NVIDIA|GeForce|RTX|GTX|Quadro') { return 'cuda' }
    }
    # AMD: 内蔵GPU（APU）は「Radeon(TM) Graphics」「780M」等の名称で専用VRAMを持たないため除外し、
    # 専用VRAM 2GB 以上のカードのみROCm候補とみなす
    foreach ($d in $devices) {
        if ($d.Name -match 'AMD|Radeon' -and
            $d.Name -notmatch 'Radeon\(TM\) Graphics|Radeon Graphics|with Radeon|\d{3}M') {
            if ($d.VramGB -ge 2) { return 'amddgpu' }
        }
    }
    return 'cpu'
}

# 搭載RAMをGB単位で返す（検出失敗時は16を返し、既定の保守的な設定に倒す）
function Get-RamGB {
    try {
        return [int][math]::Round((Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory / 1GB)
    } catch { return 16 }
}
