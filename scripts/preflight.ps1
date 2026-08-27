# preflight.ps1 - 環境チェック（OS / ポート / RAM）
# - ポートは 8080 から順に空きを探し、最初に見つかった空きポートを結果に含める
#   （8080 が占有されていても 8081 以降を自動利用する）
# 結果を INI 形式で出力する（Inno Setup 側で GetIniString/GetIniInt により読む）
# 終了コード: 0 = チェックOK / 1 = 問題あり
param(
    [string]$IniPath,
    [int]$Port = 8080
)

$ErrorActionPreference = 'SilentlyContinue'

# --- OSチェック（Windows 10/11 はバージョン 10.0、64bitのみ） ---
$os = Get-CimInstance Win32_OperatingSystem
$osOk = $false
if ($os -and $os.Version -like '10.*' -and [Environment]::Is64BitOperatingSystem) {
    $osOk = $true
}

# --- ポートチェック（指定ポートから順に空きを探し、最初の空きポートを使う） ---
# v1.0.52: アップグレード時は port.txt の前回ポートから探す（-Port で開始ポートを指定）
$chosenPort = 0
for ($p = $Port; $p -lt ($Port + 20); $p++) {
    $busy = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if (-not $busy) {
        $chosenPort = $p
        break
    }
}

# --- RAM検出（モデル選択ページの表示に使用） ---
$ram = 8
$comp = Get-CimInstance Win32_ComputerSystem
if ($comp -and $comp.TotalPhysicalMemory) {
    $ram = [math]::Round($comp.TotalPhysicalMemory / 1GB)
    if ($ram -lt 4) { $ram = 4 }
}

$lines = @(
    '[preflight]',
    ('os_ok=' + $(if ($osOk) { 'yes' } else { 'no' })),
    ('port_8080_free=' + $(if ($chosenPort -eq 8080) { 'yes' } else { 'no' })),
    ('port=' + $chosenPort),
    ('ram_gb=' + $ram)
)
$lines -join "`r`n" | Out-File -FilePath $IniPath -Encoding ascii

if ($osOk -and $chosenPort -gt 0) { exit 0 } else { exit 1 }
