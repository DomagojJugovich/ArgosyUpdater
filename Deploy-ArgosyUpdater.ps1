<#
.SYNOPSIS
  Installs / updates ArgosyUpdater on a workstation from the share. Meant to run as SYSTEM from a GPO
  (Group Policy Preferences Immediate Task) on every Group Policy refresh. Windows PowerShell 5.1.

  1. robocopy <share> -> C:\Program Files\ArgosyUpdater_1_0 (only changed files, never deletes anything,
     so an empty or unreachable share cannot remove an installed updater). Files there are never locked,
     the updater runs from its copy in C:\ProgramData\ArgosyWatcher, a new version starts on next logon
     or on RESTART from _aw_command.txt.
  2. C:\ProgramData\ArgosyWatcher: created if missing, BUILTIN\Users get Modify, so every user of a PC can
     refresh the running copy (the first user who creates the folder would otherwise own it alone).
  3. Startup and desktop shortcuts, same names as "ArgosyUpdater.exe install" creates.

  Does not replace install for anything else: the Program Files folder keeps its inherited ACL (read only
  for users, the updater never writes there).

.PARAMETER Source
  Folder with ArgosyUpdater.exe, default = folder of this script (\\bepo\ArgosyUpdater).

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File \\bepo\ArgosyUpdater\Deploy-ArgosyUpdater.ps1 -WhatIf

.NOTES
  Exit codes: 0 ok, 1 copy failed, 2 source not usable. Log: C:\Windows\Temp\ArgosyUpdater_Deploy.log
  Rollback: disable the GPO task; installed files stay as they are.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Source,
    # defaults are the paths the updater uses, change only for testing
    # 64-bit Program Files also from 32-bit PowerShell, the updater uses SpecialFolder.ProgramFiles (x64 build)
    [string]$Target = (Join-Path $(if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }) 'ArgosyUpdater_1_0'),
    [string]$DataPath = (Join-Path $env:ProgramData 'ArgosyWatcher'),
    [switch]$NoShortcuts,
    [string]$LogFile = (Join-Path $env:SystemRoot 'Temp\ArgosyUpdater_Deploy.log')
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty in param() defaults in Windows PowerShell 5.1
if (-not $Source) { $Source = Split-Path -Parent $MyInvocation.MyCommand.Path }
$target      = $Target
$programData = $DataPath
$exe         = Join-Path $target 'ArgosyUpdater.exe'

function Write-Log([string]$msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $msg
    Write-Output $line
    if ($WhatIfPreference) { return }
    try {
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Move-Item $LogFile "$LogFile.old" -Force }
        Add-Content -Path $LogFile -Value $line
    } catch { }
}

function Set-Shortcut([string]$link, [string]$description) {
    if (Test-Path $link) { return }
    if (-not $PSCmdlet.ShouldProcess($link, 'Create shortcut')) { return }
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($link)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $target
    $s.IconLocation = $exe
    $s.Description = $description
    $s.Save()
    Write-Log "shortcut created: $link"
}

try {
    Write-Log ("START source=$Source target=$target" + $(if ($WhatIfPreference) { ' (WHATIF)' } else { '' }))

    # 1. files ------------------------------------------------------------------------------------------
    if (-not (Test-Path (Join-Path $Source 'ArgosyUpdater.exe'))) {
        Write-Log "ERROR source has no ArgosyUpdater.exe, nothing done"
        exit 2
    }

    $rcArgs = @($Source, $target, '/E', '/R:2', '/W:5', '/NP', '/NDL', '/NJH', '/XF', '*.log', 'Deploy-ArgosyUpdater.ps1')
    if ($WhatIfPreference) { $rcArgs += '/L' }   # list only
    $out = & robocopy.exe @rcArgs
    $rc = $LASTEXITCODE
    $out | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Log "  $($_.Trim())" }
    # robocopy: 0 nothing to do, 1 copied, 2/3 extra files; 8+ failure
    if ($rc -ge 8) {
        Write-Log "ERROR robocopy exit code $rc"
        exit 1
    }
    Write-Log "robocopy exit code $rc"

    # 2. ProgramData for the running copy -----------------------------------------------------------------
    if (-not (Test-Path $programData)) {
        if ($PSCmdlet.ShouldProcess($programData, 'Create folder')) { New-Item -ItemType Directory $programData | Out-Null; Write-Log "created $programData" }
    }
    if ((Test-Path $programData) -and $PSCmdlet.ShouldProcess($programData, 'Grant BUILTIN\Users Modify')) {
        # SID, not name: BUILTIN\Users is localized on non-English Windows
        $null = & icacls.exe $programData /grant '*S-1-5-32-545:(OI)(CI)M' /T /C /Q
        Write-Log "icacls Users Modify on $programData exit code $LASTEXITCODE"
    }

    # 3. shortcuts ----------------------------------------------------------------------------------------
    if (-not $NoShortcuts) {
        $startup = [Environment]::GetFolderPath('CommonStartup')
        $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
        Set-Shortcut (Join-Path $startup 'ArgosyUpdater.exe-Shortcut.lnk') 'Argosy updater, maintains local app'
        Set-Shortcut (Join-Path $desktop 'ArgosyWatcher.lnk') 'Argosy updater, maintains local app'
    }

    if (Test-Path $exe) { Write-Log ("END installed version " + (Get-Item $exe).VersionInfo.FileVersion) }
    exit 0
}
catch {
    Write-Log "ERROR $($_.Exception.Message)"
    exit 1
}
