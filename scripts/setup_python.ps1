# setup_python.ps1 - Python 3.12 を {AppDir}\python にポータブル導入
# - python.org の NUGet パッケージ（zip）を使用。インストーラ・レジストリ登録が不要で、
#   アンインストール/再インストールを繰り返しても壊れない
#   （MSIバンドルの登録残りによる「導入済み扱いで何も展開されない」問題を構造的に解消）
# - システムPATHは変更しない
# - 利用可能な Python 3.11/3.12 が既にあれば再利用（冪等・最短化）
# - 失敗時は原因を install.log に記録する
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$AppDir,
    [string]$TmpDir,
    [string]$Version = '3.12.10',
    [string]$ProgressFile = ''
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $AppDir 'install.log'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
function Log { param([string]$Message) "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message" | Out-File -FilePath $LogFile -Append -Encoding utf8 }
function Progress {
    param([string]$Message)
    if ($ProgressFile) {
        [System.IO.File]::AppendAllText($ProgressFile, "$Message`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

function Test-UsablePython {
    param([string]$Candidate)
    if (-not (Test-Path $Candidate)) { return $false }
    try {
        $v = & $Candidate -c 'import sys; print(f"{sys.version_info[0]}.{sys.version_info[1]}")' 2>$null
        if ($v -match '^3\.(11|12)$') { return $true }
    }
    catch { }
    return $false
}

function Find-PythonExe {
    # レジストリ（インストール済みPythonのInstallPath）
    foreach ($rp in @(
        'HKLM:\SOFTWARE\Python\PythonCore\3.12\InstallPath',
        'HKLM:\SOFTWARE\WOW6432Node\Python\PythonCore\3.12\InstallPath',
        'HKCU:\SOFTWARE\Python\PythonCore\3.12\InstallPath'
    )) {
        $val = Get-ItemProperty -Path $rp -ErrorAction SilentlyContinue
        if ($val) {
            $dir = $val.'(default)'
            if (-not $dir) { $dir = $val.ExecutablePath }
            if ($dir -and (Test-Path $dir)) {
                $c = Join-Path $dir 'python.exe'
                if (Test-UsablePython $c) { return $c }
            }
        }
    }
    # よくある導入先
    foreach ($c in @(
        (Join-Path $env:ProgramFiles 'Python312\python.exe'),
        (Join-Path $env:ProgramFiles 'Python\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        'C:\Program\python.exe',
        'C:\Python312\python.exe'
    )) {
        if (Test-UsablePython $c) { return $c }
    }
    return $null
}

try {
    Log '--- setup_python start ---'
    Progress 'preparing Python 3.12'
    $pyDir = Join-Path $AppDir 'python'
    $pyExe = Join-Path $pyDir 'tools\python.exe'   # ポータブル版（NUGet）のレイアウト
    $pyExeLegacy = Join-Path $pyDir 'python.exe'   # 旧インストーラ方式のレイアウト
    $pathFile = Join-Path $AppDir 'python-path.txt'

    # --- 既存の利用可能なPythonを探す（あれば導入をスキップ） ---
    $found = $null
    if (Test-Path $pyExe) { $found = $pyExe }
    if (-not $found -and (Test-Path $pyExeLegacy)) { $found = $pyExeLegacy }
    if (-not $found) { $found = Find-PythonExe }
    if ($found) {
        Log "using existing python: $found"
        $found | Out-File -FilePath $pathFile -Encoding ascii
        Progress "using existing python: $found"
        Progress 'PROGRESS_DONE:0'
        Log '--- setup_python done (reuse) ---'
        exit 0
    }

    # --- ダウンロード（python.org NUGet パッケージ・zip。インストーラ不要） ---
    $nupkg = Join-Path $TmpDir "python-$Version.nupkg"
    if (-not (Test-Path $nupkg)) {
        if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
            throw 'curl.exe not found (Windows 10 1803 以降が必要)'
        }
        $url = "https://www.nuget.org/api/v2/package/python/$Version"
        Log "downloading $url"
        Progress 'downloading python 3.12.10 (portable)...'
        # -sS: 進捗メーターを抑制（PowerShell 5.1 は stderr 出力をエラー扱いし、
        # ユーザーに「エラー」と誤解させるため。エラー時のみ表示する）
        # -C -: 途中で切断された場合は続きから再開（レジューム）
        # --retry-all-errors: 一時エラーでも自動再試行
        & curl.exe -sS -L --fail -C - --retry 10 --retry-all-errors --retry-delay 5 --connect-timeout 30 -o $nupkg $url
        if ($LASTEXITCODE -ne 0) {
            Remove-Item $nupkg -Force -ErrorAction SilentlyContinue
            throw "python download failed (curl exit $LASTEXITCODE)"
        }
        $size = (Get-Item $nupkg).Length
        Log "downloaded: $([math]::Round($size / 1MB, 1)) MB"
        Progress "download complete: $([math]::Round($size / 1MB, 1)) MB"
        if ($size -lt 10MB) { throw "python download looks invalid (${size} bytes) - proxy/block page の可能性" }
    }
    else {
        Log "package already exists: $nupkg (skip download)"
    }

    # --- 展開（nupkg = zip。tools/ 配下が Python 本体） ---
    Log "extracting python to $pyDir"
    Progress 'extracting python (portable)...'
    New-Item -ItemType Directory -Force -Path $pyDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $pyDir)
    if (-not (Test-Path $pyExe)) { throw "python.exe not found after extract: $pyExe" }
    $found = $pyExe

    $found | Out-File -FilePath $pathFile -Encoding ascii
    $ver = & $found --version 2>&1
    Log "installed: $ver"
    Progress "installed: $ver"
    Progress 'PROGRESS_DONE:0'
    Log '--- setup_python done ---'
    exit 0
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    Progress 'PROGRESS_DONE:1'
    exit 1
}
