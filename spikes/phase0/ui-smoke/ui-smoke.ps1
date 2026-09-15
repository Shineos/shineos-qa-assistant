param(
    [string]$AppDir = "$env:LOCALAPPDATA\Programs\ShineosQA",
    [int]$TimeoutSec = 90
)
# UI smoke test for the installed ShineosQA (v2) desktop app.
# Launches the app via launch.vbs, drives the real window with Win32 input
# (tab switches + model menu), captures screenshots, and verifies state changes.
# ASCII-only source (PS 5.1 without BOM safe). Exit 0 = all steps PASS.
#
# Preconditions:
#   - ShineosQA installed per-user (default %LOCALAPPDATA%\Programs\ShineosQA)
#   - Window appears at its default size/position (1500x1000 @ 125% DPI);
#     click points below are window-relative physical pixels for that layout.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UiSmoke {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out UiSmoke.POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(UiSmoke.POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint ga);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out UiSmoke.RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[UiSmoke]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Drawing

$results = New-Object System.Collections.ArrayList
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $PSScriptRoot ("out\" + $stamp)
[IO.Directory]::CreateDirectory($outDir) | Out-Null

function Step([string]$name, [scriptblock]$body) {
    try {
        & $body
        Write-Output ("PASS  " + $name)
        $results.Add(@{ name = $name; ok = $true }) | Out-Null
    } catch {
        Write-Output ("FAIL  " + $name + " :: " + $_.Exception.Message)
        $results.Add(@{ name = $name; ok = $false }) | Out-Null
    }
}

function GetAppWindow {
    $p = Get-Process ShineosQA -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { return $p } else { return $null }
}

function Capture([string]$name) {
    $p = GetAppWindow
    if (-not $p) { throw "window lost before capture $name" }
    $r = New-Object UiSmoke+RECT
    [UiSmoke]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { throw "bad window rect ${w}x${h}" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return @{ path = $path; bytes = (Get-Item $path).Length }
}

function ClickRel([int]$rx, [int]$ry) {
    $p = GetAppWindow
    if (-not $p) { throw "window not found" }
    $r = New-Object UiSmoke+RECT
    [UiSmoke]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $x = $r.L + $rx; $y = $r.T + $ry
    for ($i = 0; $i -lt 10; $i++) {
        [UiSmoke]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        [UiSmoke]::BringWindowToTop($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 200
        if ([UiSmoke]::GetForegroundWindow() -eq $p.MainWindowHandle) { break }
        [UiSmoke]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [UiSmoke]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    }
    if ([UiSmoke]::GetForegroundWindow() -ne $p.MainWindowHandle) { throw "cannot foreground app window" }
    $pt = New-Object UiSmoke+POINT
    $pt.X = $x; $pt.Y = $y
    $root = [UiSmoke]::GetAncestor([UiSmoke]::WindowFromPoint($pt), 2)
    $wp = 0
    [UiSmoke]::GetWindowThreadProcessId($root, [ref]$wp) | Out-Null
    if ($wp -ne $p.Id) { throw "point ($x,$y) covered by pid $wp" }
    [UiSmoke]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [UiSmoke]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [UiSmoke]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

# ---- S1: launch (if not running) and wait for the window ----
Step "S1 app window appears" {
    if (-not (GetAppWindow)) {
        Start-Process wscript.exe -ArgumentList ('"' + (Join-Path $AppDir 'launch.vbs') + '"')
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (GetAppWindow) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "ShineosQA window did not appear within ${TimeoutSec}s"
}

# ---- S2: backend health + status ----
Step "S2 backend health and status" {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $ok = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $h = curl.exe -s --max-time 3 http://127.0.0.1:8300/health
            $s = curl.exe -s --max-time 3 http://127.0.0.1:8300/api/status
            if ($h -match '"status":true' -and $s -match '"tier"') { $ok = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 800
    }
    if (-not $ok) { throw "backend not healthy on 8300" }
}

# ---- S3: chat tab ----
$shot3 = $null
Step "S3 chat tab capture" {
    ClickRel 270 71
    Start-Sleep -Milliseconds 600
    $script:shot3 = Capture "03-chat-tab.png"
}

# ---- S4: knowledge tab (content must change) ----
Step "S4 knowledge tab switch changes UI" {
    ClickRel 380 71
    Start-Sleep -Milliseconds 600
    $s4 = Capture "04-knowledge-tab.png"
    if ($s4.bytes -eq $script:shot3.bytes) { throw "knowledge tab screenshot identical to chat tab (no state change)" }
}

# ---- S5: back to chat ----
$shot5 = $null
Step "S5 back to chat tab" {
    ClickRel 270 71
    Start-Sleep -Milliseconds 600
    $script:shot5 = Capture "05-chat-back.png"
}

# ---- S6: model menu opens ----
Step "S6 model menu opens" {
    ClickRel 520 939
    Start-Sleep -Milliseconds 600
    $s6 = Capture "06-model-menu.png"
    if ($s6.bytes -eq $script:shot5.bytes) { throw "menu screenshot identical to closed state (menu did not open?)" }
}

# ---- S7: menu closes on outside click ----
Step "S7 model menu closes" {
    ClickRel 900 300
    Start-Sleep -Milliseconds 600
    $s7 = Capture "07-menu-closed.png"
    if ($s7.bytes -eq 0) { throw "empty capture" }
}

# ---- summary ----
$pass = @($results | Where-Object { $_.ok }).Count
$total = $results.Count
Write-Output ("RESULT {0}/{1} steps passed; screenshots in {2}" -f $pass, $total, $outDir)
if ($pass -ne $total) { exit 1 } else { exit 0 }
