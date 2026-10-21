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
# output is removed here: settings.json, sensors.txt, notes\ and sessions\ are
# the user's data.
Get-ChildItem $output -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.dll' -or $_.Name -in 'DeskMon.deps.json', 'DeskMon.runtimeconfig.json' } |
    Remove-Item -Force

Write-Host "Building..." -ForegroundColor Cyan
dotnet publish $project --configuration Release --output $output --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Built: $(Join-Path $output 'DeskMon.exe')" -ForegroundColor Green

if ($wasRunning -or (Get-ScheduledTask -TaskName 'DeskMon' -ErrorAction SilentlyContinue)) {
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
        Write-Host "DeskMon did not start - check app\error.log" -ForegroundColor Red
    }
}
