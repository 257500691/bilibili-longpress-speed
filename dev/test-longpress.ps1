# End-to-end tests for BiliLongPress.
#
# It simulates REAL mouse input (SendInput) over the bilibili player window and reads the
# video's playbackRate back through the Chrome DevTools Protocol, verifying that:
#   A. a short click still reaches the page untouched (mousedown/mouseup/click all +1)
#   B. a long press boosts playback and restores the previous rate on release,
#      while the mouse-up is swallowed so the player never sees a click
#   C. pressing and then dragging does NOT boost
#   D. long pressing on the control bar does NOT boost
#
# Prereqs:
#   - client running with --remote-debugging-port=9222 and a video open in the player window
#   - node in PATH (Node.js 22+, uses the built-in WebSocket/fetch)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\dev\test-longpress.ps1
#   powershell -ExecutionPolicy Bypass -File .\dev\test-longpress.ps1 -Exe C:\path\BiliLongPress.exe

param(
    [string]$Exe  = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\BiliLongPress.exe'),
    [string]$Node = 'node'
)

$ErrorActionPreference = 'Stop'
$Dev    = $PSScriptRoot
$Dist   = Split-Path -Parent $Exe
$Report = Join-Path $Dist 'probe-report.txt'
$Log    = Join-Path $env:LOCALAPPDATA 'BiliLongPress\log.txt'

if (-not (Test-Path $Exe)) { throw "not found: $Exe  (build it first: powershell -File .\build.ps1)" }

Add-Type -Namespace T -Name M -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
[DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
public static void Down() { var a = new INPUT[1]; a[0].type = 0; a[0].mi.dwFlags = 0x0002; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
public static void Up()   { var a = new INPUT[1]; a[0].type = 0; a[0].mi.dwFlags = 0x0004; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }
'@

function Ev([string]$file) {
    $path = Join-Path $Dev $file
    (& $Node (Join-Path $Dev 'cdp.mjs') eval 'player.html' "@$path" 2>&1 | Out-String) -replace '\s+', ' '
}
function Rate {
    $s = Ev 'state.js'
    if ($s -match '"rate":\s*([\d.]+)') { return $Matches[1] }
    return '?'
}
function Counters { Ev 'read-counters.js' }

# --- 1. hot zone geometry ---------------------------------------------------
Write-Host "probing hot zone ..."
Remove-Item $Report -ErrorAction SilentlyContinue
Start-Process -FilePath $Exe -ArgumentList '--probe' -Wait -RedirectStandardOutput (Join-Path $Dist 'out.txt')
$rep = Get-Content $Report -Raw -Encoding UTF8
$m = [regex]::Match($rep, 'X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)')
if (-not $m.Success) { throw "hot zone not found; probe report:`n$rep" }
$L = [int]$m.Groups[1].Value; $T = [int]$m.Groups[2].Value
$W = [int]$m.Groups[3].Value; $H = [int]$m.Groups[4].Value
$cx = $L + [int]($W / 2); $cy = $T + [int]($H / 2); $barY = $T + $H + 40
Write-Host "hot zone = $L,$T ${W}x${H}   center = $cx,$cy   control bar = $cx,$barY"

# --- 2. restart the tool with verbose logging -------------------------------
Get-Process -Name BiliLongPress -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item $Log -ErrorAction SilentlyContinue
$tool = Start-Process -FilePath $Exe -ArgumentList '--verbose' -PassThru
Start-Sleep -Seconds 4
Write-Host "tool pid = $($tool.Id)"
& $Node (Join-Path $Dev 'cdp.mjs') front 'player.html' | Out-Null
Start-Sleep -Milliseconds 700
Write-Host "context : $(Ev 'context-info.js')"
Write-Host "counters: $(Ev 'install-counters.js')"

# --- A. short click ---------------------------------------------------------
Write-Host "`n--- A. short click (120ms): the page must still receive the click ---"
[void][T.M]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 150
[T.M]::Down(); Start-Sleep -Milliseconds 120; [T.M]::Up(); Start-Sleep -Milliseconds 500
Write-Host "  counters after click : $(Counters)"

# --- B. long press ----------------------------------------------------------
Write-Host "`n--- B. long press 1000ms: boost then restore, and no click delivered ---"
[void][T.M]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 150
Write-Host "  rate before : $(Rate)"
[T.M]::Down()
Start-Sleep -Milliseconds 450; Write-Host "  rate @450ms : $(Rate)"
Start-Sleep -Milliseconds 350; Write-Host "  rate @800ms : $(Rate)"
[T.M]::Up()
Start-Sleep -Milliseconds 900; Write-Host "  rate after  : $(Rate)"
Write-Host "  counters    : $(Counters)"

# --- C. drag -----------------------------------------------------------------
Write-Host "`n--- C. press + drag 120px, hold 1000ms: must NOT boost ---"
[void][T.M]::SetCursorPos($cx, $cy); Start-Sleep -Milliseconds 150
[T.M]::Down(); Start-Sleep -Milliseconds 80
[void][T.M]::SetCursorPos($cx + 120, $cy + 40)
Start-Sleep -Milliseconds 450; Write-Host "  rate @500ms : $(Rate)"
Start-Sleep -Milliseconds 500
[T.M]::Up(); Start-Sleep -Milliseconds 600
Write-Host "  rate after  : $(Rate)"

# --- D. control bar ---------------------------------------------------------
Write-Host "`n--- D. long press on the control bar: must NOT boost ---"
[void][T.M]::SetCursorPos($cx, $barY); Start-Sleep -Milliseconds 150
[T.M]::Down(); Start-Sleep -Milliseconds 900
Write-Host "  rate @900ms : $(Rate)"
[T.M]::Up(); Start-Sleep -Milliseconds 400
Write-Host "  rate after  : $(Rate)"

Write-Host "`n--- tool log ---"
Get-Content $Log -Encoding UTF8 | Select-Object -Last 20
