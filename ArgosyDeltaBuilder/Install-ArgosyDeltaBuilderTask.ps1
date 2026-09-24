#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Registers ArgosyDeltaBuilder as a Scheduled Task on the file server (Windows PowerShell 5.1, ScheduledTasks module).

.EXAMPLE
  .\Install-ArgosyDeltaBuilderTask.ps1 -WhatIf
  .\Install-ArgosyDeltaBuilderTask.ps1 -User 'DU\gmsa-argdelta$'

.NOTES
  Rollback: Unregister-ScheduledTask -TaskName ArgosyDeltaBuilder -Confirm:$false
            then remove <ShareRoot>\_DELTA, clients fall back to plain file sync of EXEDIR.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallDir = $PSScriptRoot,
    [string]$TaskName = 'ArgosyDeltaBuilder',
    [int]$IntervalMinutes = 5,
    # SYSTEM (use a local ShareRoot path in ArgosyDeltaBuilder.json) or a gMSA ending with '$'
    [string]$User = 'SYSTEM'
)

$exe = Join-Path $InstallDir 'ArgosyDeltaBuilder.exe'
$conf = Join-Path $InstallDir 'ArgosyDeltaBuilder.json'
if (-not (Test-Path $exe)) { throw "Not found: $exe" }
if (-not (Test-Path $conf)) { throw "Not found: $conf" }

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $InstallDir
# no -RepetitionDuration = repeat indefinitely (Windows Server 2016+)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 4)

if ($User -eq 'SYSTEM') {
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
} elseif ($User.EndsWith('$')) {
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Password -RunLevel Highest
} else {
    throw "Use SYSTEM or a gMSA (name ending with '$'). A regular account needs a stored password, register it manually."
}

if ($PSCmdlet.ShouldProcess("$TaskName ($exe, every $IntervalMinutes min, as $User)", 'Register-ScheduledTask')) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    $t = Get-ScheduledTask -TaskName $TaskName
    "Registered $TaskName, repetition: interval=$($t.Triggers[0].Repetition.Interval) duration=$($t.Triggers[0].Repetition.Duration)"
    "First run by hand: & '$exe' --whatif"
}
