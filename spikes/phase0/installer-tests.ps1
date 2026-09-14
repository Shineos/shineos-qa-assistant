param(
    [string]$Phase = "d1",
    [string]$AppDir = "$env:LOCALAPPDATA\Programs\ShineosQA",
    [string]$UpgradeExe = "D:\dev\shineos-local-ai\dist-test\ShineosQA-Setup-2.0.1.exe",
    [string]$ModelFile = "Qwen3-1.7B-IQ4_XS.gguf"
)
# Installer failure-path / upgrade-path tests (task D).
#   d1: silent upgrade 2.0.0 -> 2.0.1 (exit 0, knowledge data kept, app healthy)
#   d2: corrupted model file -> chat fails gracefully (SSE error event, backend alive);
#       restore bytes -> chat works again
#   d3: interactive uninstall (UIA answers "No" to the data prompt) -> data kept, app removed
# ASCII-only source (PS 5.1 without BOM safe). Exit 0 = phase passed.
$ErrorActionPreference = 'Stop'

function Close-App {
    $p = Get-Process ShineosQA -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    if ($p) { $null = $p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null; if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    Start-Sleep -Seconds 2
    foreach ($n in 'ShineosQA.Backend', 'llama-server') {
        Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1
}

function Wait-Healthy([int]$sec = 120) {
    $deadline = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $deadline) {
        try {
            $h = curl.exe -s --max-time 3 http://127.0.0.1:8300/health
            if ($h -match '"status":true') { return $true }
        } catch { }
        Start-Sleep -Milliseconds 800
    }
    return $false
}

if ($Phase -eq 'd1') {
    # ---- D1: silent upgrade ----
    if (-not (Test-Path $UpgradeExe)) { Write-Output "FAIL upgrade exe not found: $UpgradeExe"; exit 1 }
    $dataBefore = Test-Path (Join-Path $AppDir 'data\knowledge.db')
    $chunksBefore = $null
    if ($dataBefore) {
        $s = curl.exe -s --max-time 5 http://127.0.0.1:8300/api/status
        if ($s -match '"chunks":(\d+)') { $chunksBefore = $Matches[1] }
    }
    Close-App
    $p = Start-Process $UpgradeExe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
    if ($p.ExitCode -ne 0) { Write-Output ("FAIL upgrade exit=" + $p.ExitCode); exit 1 }
    if (-not (Test-Path (Join-Path $AppDir 'data\knowledge.db'))) { Write-Output 'FAIL knowledge.db lost on upgrade'; exit 1 }
    $ver = (Get-ItemProperty ('HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9A2C6D71-4B3E-4F8A-9C15-D2E4B7A81F21}_is1') -ErrorAction SilentlyContinue).DisplayVersion
    if ($ver -ne '2.0.1') { Write-Output ("FAIL DisplayVersion=" + $ver + " expected 2.0.1"); exit 1 }
    Start-Process wscript.exe -ArgumentList ('"' + (Join-Path $AppDir 'launch.vbs') + '"')
    if (-not (Wait-Healthy)) { Write-Output 'FAIL backend not healthy after upgrade'; exit 1 }
    $s2 = curl.exe -s --max-time 5 http://127.0.0.1:8300/api/status
    if ($s2 -notmatch '"chunks":(\d+)') { Write-Output 'FAIL status after upgrade'; exit 1 }
    if ($chunksBefore -ne $null -and $Matches[1] -ne $chunksBefore) { Write-Output ("WARN chunk count changed: " + $chunksBefore + " -> " + $Matches[1]) }
    Write-Output ("PASS d1 upgrade exit=0 version=2.0.1 data kept chunks=" + $Matches[1])
    exit 0
}

if ($Phase -eq 'd2') {
    # ---- D2: corrupted model ----
    $model = Join-Path $AppDir ("models\" + $ModelFile)
    if (-not (Test-Path $model)) { Write-Output "FAIL model not found: $model"; exit 1 }
    if (-not (Wait-Healthy 1)) {
        Start-Process wscript.exe -ArgumentList ('"' + (Join-Path $AppDir 'launch.vbs') + '"')
        if (-not (Wait-Healthy)) { Write-Output 'FAIL backend not healthy'; exit 1 }
    }
    Close-App
    # 破損: ヘッダ直後の4バイトを反転。try/finally で失敗時も必ず復元する
    $orig = $null
    $verdict = $null
    try {
        $fs = $null
        for ($i = 0; $i -lt 10 -and -not $fs; $i++) {
            try { $fs = [System.IO.File]::Open($model, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read) }
            catch { Start-Sleep -Milliseconds 800 }
        }
        if (-not $fs) { throw 'cannot open model file (locked?)' }
        $fs.Seek(1024, [System.IO.SeekOrigin]::Begin) | Out-Null
        $orig = New-Object byte[] 4
        $null = $fs.Read($orig, 0, 4)
        $fs.Seek(1024, [System.IO.SeekOrigin]::Begin) | Out-Null
        $fs.Write([byte[]](0xDE, 0xAD, 0xBE, 0xEF), 0, 4)
        $fs.Close()

        Start-Process wscript.exe -ArgumentList ('"' + (Join-Path $AppDir 'launch.vbs') + '"')
        Start-Sleep -Seconds 6
        if (-not (Wait-Healthy)) { throw 'backend died with corrupted model (should stay up)' }

        # chat attempt: with the pre-start SHA check the SHINE_E_MODEL_HASH SSE error
        # must come back FAST (seconds). the old implementation hung 240s+ with no reply.
        $body = '{"chat_uuid":"corrupt-test","message":"日当はいくらですか"}'
        $tmp = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($tmp, $body)
        $out = [IO.Path]::GetTempFileName()
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        curl.exe -s -N --max-time 120 -X POST http://127.0.0.1:8300/api/chat -H "Content-Type: application/json; charset=utf-8" --data-binary "@$tmp" -o $out | Out-Null
        $sw.Stop()
        $txt = [IO.File]::ReadAllText($out)
        Remove-Item $tmp, $out -Force
        $sseError = ($txt -match 'event: error') -and ($txt -match 'SHINE_E_MODEL_HASH')

        # pass criteria: 1) fast SHINE_E_MODEL_HASH SSE error (<60s) 2) backend alive
        $alive = Wait-Healthy 5
        if (-not $sseError) { $verdict = 'FAIL corrupted model did not return a fast SHINE_E_MODEL_HASH SSE error' }
        elseif ($sw.ElapsedMilliseconds -gt 60000) { $verdict = ('FAIL SSE error too slow: ' + $sw.ElapsedMilliseconds + 'ms (expected < 60000)') }
        elseif (-not $alive) { $verdict = 'FAIL backend died during corrupted-model chat' }
        else { $verdict = ('PASS d2 corrupted model -> SHINE_E_MODEL_HASH SSE error in ' + $sw.ElapsedMilliseconds + 'ms, backend alive, restore works') }
    }
    finally {
        # 必ず復元して再起動
        Close-App
        if ($orig) {
            $fs = $null
            for ($i = 0; $i -lt 10 -and -not $fs; $i++) {
                try { $fs = [System.IO.File]::Open($model, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read) }
                catch { Start-Sleep -Milliseconds 800 }
            }
            if ($fs) {
                $fs.Seek(1024, [System.IO.SeekOrigin]::Begin) | Out-Null
                $fs.Write($orig, 0, 4)
                $fs.Close()
            }
        }
        Start-Process wscript.exe -ArgumentList ('"' + (Join-Path $AppDir 'launch.vbs') + '"')
        $null = Wait-Healthy 180
    }
    Write-Output $verdict
    if ($verdict -notmatch '^PASS') { exit 1 }
    exit 0
}

if ($Phase -eq 'd3') {
    # ---- D3: interactive uninstall (answer No to data prompt) ----
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class WinEnum {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static List<long> WindowsOf(uint pid) {
        var list = new List<long>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) list.Add(h.ToInt64());
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@
    Close-App
    # 教訓: 残存インスタンスがあると unins000.dat 排他で破綻する。必ず掃除してから単一起動する
    Get-Process unins000 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Start-Process (Join-Path $AppDir 'unins000.exe')
    # アンインストーラのウィザード／確認ダイアログを待つ（全可視ウィンドウを走査）
    $hwnd = [long]0
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and $hwnd -eq 0) {
        Start-Sleep -Milliseconds 700
        $proc = Get-Process unins000 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            foreach ($h in [WinEnum]::WindowsOf($proc.Id)) { if ($h -ne 0) { $hwnd = $h; break } }
        }
    }
    if ($hwnd -eq 0) { Write-Output 'FAIL uninstaller dialog did not appear'; exit 1 }
    function Find-Button([long]$h, [string]$pattern) {
        $r = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$h)
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($b in $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) {
            if ($b.Current.Name -match $pattern) { return $b }
        }
        return $null
    }
    # 第1ダイアログ: アンインストール確認（はい/いいえ）→「はい」を選択
    $yes = Find-Button $hwnd 'はい|Yes|OK'
    if (-not $yes) { Write-Output 'FAIL confirm "Yes" button not found'; exit 1 }
    $yes.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    # 第2ダイアログ: データ削除の確認（カスタムMsgBox）→「いいえ」。
    # ウィザード確定後に現れる、いいえボタンを持つ新ウィンドウを待つ
    $no = $null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $no) {
        Start-Sleep -Milliseconds 700
        $proc = Get-Process unins000 -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            foreach ($h in [WinEnum]::WindowsOf($proc.Id)) {
                if ($h -eq $hwnd) { continue }
                $no = Find-Button $h 'いいえ|No'
                if ($no) { break }
            }
        }
    }
    if (-not $no) { Write-Output 'FAIL data-prompt "No" button not found'; exit 1 }
    $no.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    # アンインストール完了を待つ
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process unins000 -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 800
    }
    Start-Sleep -Seconds 2
    $appGone = -not (Test-Path (Join-Path $AppDir 'ShineosQA.Backend.exe'))
    $dataKept = Test-Path (Join-Path $AppDir 'data\knowledge.db')
    if (-not $appGone) { Write-Output 'FAIL app files still present after uninstall'; exit 1 }
    if (-not $dataKept) { Write-Output 'FAIL knowledge.db deleted despite answering No'; exit 1 }
    Write-Output 'PASS d3 interactive uninstall: app removed, data kept (No)'
    exit 0
}

Write-Output "unknown phase: $Phase (use d1|d2|d3)"
exit 2
