<#
    Removes the logon task created by Install-Autostart.ps1 and stops the widget.
    Nothing else is touched - the program itself stays in .\app.

    Run as administrator:
        powershell -ExecutionPolicy Bypass -File Uninstall-Autostart.ps1
#>

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Needs administrator rights - re-launching elevated..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-ExecutionPolicy', 'Bypass', '-NoProfile', '-File', "`"$PSCommandPath`""
    )
    return
}

if (Get-ScheduledTask -TaskName 'DeskMon' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'DeskMon' -Confirm:$false
    Write-Host "Removed scheduled task 'DeskMon'." -ForegroundColor Green
} else {
    Write-Host "No scheduled task named 'DeskMon' was registered."
}

Get-Process DeskMon -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill()
    Write-Host "Stopped running DeskMon (pid $($_.Id))."
}
