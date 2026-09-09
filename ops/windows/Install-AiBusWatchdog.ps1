$ErrorActionPreference = 'Stop'
$taskName = 'AiBus Local Watchdog'
$watchdogPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Ensure-AiBus.ps1'))
$powershellPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

if (-not (Test-Path -LiteralPath $watchdogPath)) { throw "Watchdog script was not found: $watchdogPath" }

$arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$watchdogPath`""
$action = New-ScheduledTaskAction -Execute $powershellPath -Argument $arguments
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $userId
$repeating = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 4)
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon,$repeating) -Settings $settings -Principal $principal -Description 'Starts Docker Desktop and restores the local AiBus stack when its health endpoint is unavailable.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Get-ScheduledTask -TaskName $taskName | Select-Object TaskName,State,Description
