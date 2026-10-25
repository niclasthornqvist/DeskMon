<#
    Builds DeskMon into E:\Nicke\Program\app, then restarts it.
    Run this after editing anything under src\.

        right-click -> Run with PowerShell
    or: powershell -ExecutionPolicy Bypass -File Build.ps1

    Self-elevates, because the running widget is elevated (CPU temperature
    driver) and a normal user process can neither stop it nor overwrite the
    files it has open.
#>

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Needs administrator rights - re-launching elevated..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-ExecutionPolicy', 'Bypass', '-NoProfile', '-NoExit', '-File', "`"$PSCommandPath`""
    )
    return
}

# dotnet is installed machine-wide but a fresh elevated shell may not have it
# on PATH yet if the installer ran during this session.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:Path = "C:\Program Files\dotnet;$env:Path"
}

$root    = $PSScriptRoot
$project = Join-Path $root 'src\DeskMon\DeskMon.csproj'
$output  = Join-Path $root 'app'

# Ask DeskMon to close itself before resorting to Kill, so it flushes the notes
# and its window position on the way out.
#
# Process.CloseMainWindow() cannot do this: the widget is a tool window kept out
# of the taskbar, so MainWindowHandle comes back as 0 and the call does nothing.
# WM_CLOSE has to go to the window itself, found by the class name WPF gives it.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class DeskMonWindow {
  delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

  /// <summary>
  /// Posts WM_CLOSE to the widget of this process. False if it has no window.
  ///
  /// The widget by its title, not just the first DeskMon window: the notepad
  /// and the FPS overlay are DeskMon windows too, and during a game one of
  /// them is usually on top. Asked to close, they only hide - so the widget
  /// was then killed after the wait, and the session it was recording was
  /// never written. Visible or not: a widget hidden from the tray or during a
  /// game still has to be asked.
  /// </summary>
  public static bool RequestClose(uint targetPid) {
    IntPtr hit = IntPtr.Zero;
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != targetPid) return true;
      var name = new StringBuilder(128); GetClassName(h, name, 128);
      if (!name.ToString().StartsWith("HwndWrapper[DeskMon", StringComparison.OrdinalIgnoreCase)) return true;
      var title = new StringBuilder(64); GetWindowText(h, title, 64);
      if (title.ToString() == "DeskMon") {
        hit = h; return false;
      }
      return true;
    }, IntPtr.Zero);

    if (hit == IntPtr.Zero) return false;
    return PostMessage(hit, 0x0010, IntPtr.Zero, IntPtr.Zero);
  }
}
'@

$wasRunning = [bool](Get-Process DeskMon -ErrorAction SilentlyContinue)

# Starts DeskMon again the way it was running - through its logon task when
# there is one, so it comes back elevated - and says whether it came up.
# A function because a failed build needs it too: see Stop-WithFailure.
function Start-DeskMon {
    if (-not ($wasRunning -or (Get-ScheduledTask -TaskName 'DeskMon' -ErrorAction SilentlyContinue))) {
        return
    }
    Write-Host "Restarting DeskMon..." -ForegroundColor Cyan
    if (Get-ScheduledTask -TaskName 'DeskMon' -ErrorAction SilentlyContinue) {
        Start-ScheduledTask -TaskName 'DeskMon'
    } else {
        Start-Process (Join-Path $output 'DeskMon.exe')
    }
    Start-Sleep -Seconds 3
    if (Get-Process DeskMon -ErrorAction SilentlyContinue) {
        Write-Host "DeskMon is running." -ForegroundColor Green
    } else {
        Write-Host "DeskMon did not start - check error.log in $env:APPDATA\DeskMon" -ForegroundColor Red
    }
}

# The exe as it was before this build, kept outside app\ so a publish that
# fails half-way - a locked file, a full disk - can't leave a broken one behind.
$previousExe = Join-Path $env:TEMP 'DeskMon-previous.exe'
$builtExe    = Join-Path $output 'DeskMon.exe'

# A build that fails after DeskMon was closed used to just exit: DeskMon
# stayed closed, and when started from the widget's BUILD button the window
# closed with it, so nothing said why. Now the previous exe is put back and
# started again, and the window waits so the error can be read.
function Stop-WithFailure([string]$why) {
    Write-Host ""
    Write-Host "Build FAILED: $why" -ForegroundColor Red
    if (Test-Path $previousExe) {
        try {
            Copy-Item $previousExe $builtExe -Force
            Write-Host "Put the previous DeskMon.exe back." -ForegroundColor Yellow
        } catch {
            Write-Host "Couldn't put the previous DeskMon.exe back: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    try { Start-DeskMon } catch { Write-Host "Couldn't restart DeskMon: $($_.Exception.Message)" -ForegroundColor Red }
    Write-Host ""
    Read-Host "Press Enter to close" | Out-Null
    exit 1
}

foreach ($p in @(Get-Process DeskMon -ErrorAction SilentlyContinue)) {
    $asked = $false
    try { $asked = [DeskMonWindow]::RequestClose([uint32]$p.Id) } catch { }

    if ($asked) {
        Write-Host "Asked DeskMon (pid $($p.Id)) to close..."
        if ($p.WaitForExit(5000)) {
            Write-Host "  closed cleanly." -ForegroundColor DarkGray
            continue
        }
        Write-Host "  did not close in 5s; killing." -ForegroundColor Yellow
    }

    Write-Host "Stopping DeskMon (pid $($p.Id))..."
    $p.Kill()
    $p.WaitForExit(5000) | Out-Null
}

# Everything from here on runs with DeskMon closed, so any error goes through
# Stop-WithFailure rather than ending the script with the widget gone.
try {

# A copy left by an earlier run is an older build: never the one to put back.
Remove-Item $previousExe -Force -ErrorAction SilentlyContinue
if (Test-Path $builtExe) {
    Copy-Item $builtExe $previousExe -Force
}

# Only ever a leftover child of ours, with nothing to flush.
Get-Process presentmon -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping $($_.ProcessName) (pid $($_.Id))..."
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}
Start-Sleep -Milliseconds 500

# Killing PresentMon leaves its ETW trace session running with no owner, and
# orphans like that eventually starve new captures of data.
$stale = & logman query -ets 2>&1 | Select-String -Pattern '^DeskMon\s'
if ($stale) {
    Write-Host "Clearing orphaned ETW session 'DeskMon'..."
    & logman stop DeskMon -ets | Out-Null
}

# The single-file exe unpacks its two native helpers (LibreHardwareMonitor's
# Mono.Posix ones) into %TEMP%\.net\DeskMon\<hash> the first time each build
# starts, and each build gets a new hash, so every rebuild used to leave
# another 1.6 MB folder behind for good. DeskMon is stopped by now, so nothing has them open; the
# next start unpacks one fresh copy.
$extracted = Join-Path $env:TEMP '.net\DeskMon'
if (Test-Path $extracted) {
    Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue
}

# publish never deletes from the output folder, so the loose DLLs from the
# multi-file layout would sit next to the single-file exe forever. Only build
# output is removed here. The user's data lives in %APPDATA%\DeskMon since
# 1.65; on a machine not yet moved, app\ still holds settings.json, notes\ and
# sessions\, and DeskMon moves them itself on its next start.
Get-ChildItem $output -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.dll' -or $_.Name -in 'DeskMon.deps.json', 'DeskMon.runtimeconfig.json' } |
    Remove-Item -Force

# Whether a file can be opened with nobody else holding it.
function Test-FileFree([string]$path) {
    try {
        $stream = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $true
    } catch {
        return $false
    }
}

# A process that has exited can still hold its exe for a moment while
# Windows tears it down, and Defender often scans it right then - so the
# publish sometimes found DeskMon.exe "being used by another process". Up to
# 10 s for it to come free, rather than a fixed pause that is sometimes short.
$freeBy = (Get-Date).AddSeconds(10)
while ((Test-Path $builtExe) -and -not (Test-FileFree $builtExe) -and (Get-Date) -lt $freeBy) {
    Start-Sleep -Milliseconds 250
}

# Tried again when what failed was a file in use - a virus scan or the
# editor's C# extension holding something under obj\ for a moment - since
# that clears up by itself. Any other failure is real, and stops at once.
# Only stdout is captured: with ErrorActionPreference Stop, Windows
# PowerShell would turn dotnet's stderr lines into a terminating error.
Write-Host "Building..." -ForegroundColor Cyan
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $lines = @()
    dotnet publish $project --configuration Release --output $output --nologo | Tee-Object -Variable lines
    if ($LASTEXITCODE -eq 0) {
        break
    }
    if ($attempt -eq 3 -or -not ($lines -match 'being used by another process|MSB3021|MSB3027')) {
        Stop-WithFailure "dotnet publish exited with code $LASTEXITCODE - see the output above."
    }
    Write-Host "A file was in use - trying again in 3 s ($attempt of 3)..." -ForegroundColor Yellow
    Start-Sleep -Seconds 3
}

} catch {
    Stop-WithFailure $_.Exception.Message
}

Write-Host ""
Write-Host "Built: $builtExe" -ForegroundColor Green
Remove-Item $previousExe -Force -ErrorAction SilentlyContinue

# app\ writable only by Administrators and SYSTEM, readable by everyone else.
# DeskMon.exe is started elevated at logon by its scheduled task, so a folder
# any program could write to let any program put its own exe there and have it
# run as admin with no prompt - S1 in the security review. Its data moved out
# to %APPDATA% in 1.65, so nothing ordinary needs to write here any more; this
# script and DeskMon itself both run elevated. By SID, not by name: the names
# are translated on a Windows in another language. Every build, so a
# folder recreated or reset is locked again. A failure is said, not fatal.
# The folder only, then everything in it reset to inherit from it. Not /T on
# the grant: that put the folder's inheritable entries on DeskMon.exe too,
# where (OI)(CI) mean nothing - the exe was left with no usable permissions
# and Windows refused to start it for anyone.
& icacls $output /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /C /Q | Out-Null
$locked = $LASTEXITCODE -eq 0
& icacls (Join-Path $output '*') /reset /T /C /Q | Out-Null
if ($locked -and $LASTEXITCODE -eq 0) {
    Write-Host "Locked $output - only administrators can change it." -ForegroundColor DarkGray
} else {
    Write-Host "Couldn't lock $output (icacls exit code $LASTEXITCODE) - it stays writable by every user." -ForegroundColor Yellow
}

Start-DeskMon
