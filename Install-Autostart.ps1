<#
    Makes DeskMon start at logon.

    Why a Scheduled Task instead of the Startup folder or a Run registry key:
    DeskMon needs administrator rights (the CPU temperature driver), and both of
    those methods would pop a UAC prompt every single time you log in. A task
    registered with the highest privileges starts it silently.

    Run this ONCE, as administrator:
        right-click -> Run with PowerShell  (and accept the prompt)
    or from an elevated PowerShell:
        powershell -ExecutionPolicy Bypass -File Install-Autostart.ps1
#>

$ErrorActionPreference = 'Stop'

$taskName = 'DeskMon'
$exe      = Join-Path $PSScriptRoot 'app\DeskMon.exe'

if (-not (Test-Path $exe)) {
    throw "DeskMon.exe not found at $exe - run Build.ps1 first."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
          ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Needs administrator rights - re-launching elevated..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-ExecutionPolicy', 'Bypass', '-NoProfile', '-File', "`"$PSCommandPath`""
    )
    return
}

$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# RunLevel Highest = starts elevated with no UAC prompt.
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                                        -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

# Let the desktop finish appearing first; starting a widget mid-logon just makes
# the logon feel slower and the window can land before the shell is ready.
$trigger.Delay = 'PT15S'

Register-ScheduledTask -TaskName $taskName `
                       -Action $action `
                       -Trigger $trigger `
                       -Principal $principal `
                       -Settings $settings `
                       -Description 'Desktop hardware monitor widget (CPU/RAM/SWAP/temps).' `
                       -Force | Out-Null

Write-Host ""
Write-Host "Installed scheduled task '$taskName'." -ForegroundColor Green
Write-Host "  Runs:     $exe"
Write-Host "  Trigger:  at logon for $env:USERNAME, 15 s delay, elevated, no UAC prompt"
Write-Host ""
Write-Host "Starting it now to check..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3
if (Get-Process DeskMon -ErrorAction SilentlyContinue) {
    Write-Host "DeskMon is running." -ForegroundColor Green
} else {
    Write-Host "DeskMon did not start - check app\error.log" -ForegroundColor Red
}
