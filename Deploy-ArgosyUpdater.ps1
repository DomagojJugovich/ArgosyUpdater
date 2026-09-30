<#
.SYNOPSIS
  Installs / updates ArgosyUpdater on a workstation from the share. Meant to run as SYSTEM from a GPO
  (Group Policy Preferences Immediate Task) on every Group Policy refresh. Windows PowerShell 5.1.

  All the work is done by "ArgosyUpdater.exe install" from the share (same as _ArgosyUpdaterInstall.bat):
  copy to C:\Program Files\ArgosyUpdater_1_0, rights on Program Files / ProgramData\ArgosyWatcher,
  startup + common desktop shortcut. This script only decides if install is needed, because install
  copies every file unconditionally (~10 MB on every GP refresh of every PC otherwise):
    - ArgosyUpdater.exe or one of the shortcuts is missing, or
    - robocopy /L finds a file on the share that is newer / missing / different in Program Files
      (this script itself is not compared).

  Files in Program Files are never locked, the updater runs from its copy in C:\ProgramData\ArgosyWatcher,
  a new version starts on next logon or on RESTART from _aw_command.txt.

.PARAMETER Source
  Folder with ArgosyUpdater.exe, default = folder of this script (\\bepo\ArgosyUpdater).

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File \\bepo\ArgosyUpdater\Deploy-ArgosyUpdater.ps1 -WhatIf

.NOTES
  Exit codes: 0 up to date or installed, 1 install failed, 2 source not usable.
  Log: C:\Windows\Temp\ArgosyUpdater_Deploy.log. Rollback: disable the GPO task, installed files stay.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Source,
    [string]$LogFile = (Join-Path $env:SystemRoot 'Temp\ArgosyUpdater_Deploy.log')
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty in param() defaults in Windows PowerShell 5.1
if (-not $Source) { $Source = Split-Path -Parent $MyInvocation.MyCommand.Path }

# same paths as the updater: SpecialFolder.ProgramFiles of the x64 build, CommonStartup, CommonDesktopDirectory
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$target       = Join-Path $programFiles 'ArgosyUpdater_1_0'
$sourceExe    = Join-Path $Source 'ArgosyUpdater.exe'
$shortcuts    = @(
    (Join-Path ([Environment]::GetFolderPath('CommonStartup')) 'ArgosyUpdater.exe-Shortcut.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'ArgosyWatcher.lnk')
)

function Write-Log([string]$msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $msg
    Write-Output $line
    if ($WhatIfPreference) { return }
    try {
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Move-Item $LogFile "$LogFile.old" -Force }
        Add-Content -Path $LogFile -Value $line
    } catch { }
}

try {
    Write-Log ("START source=$Source target=$target" + $(if ($WhatIfPreference) { ' (WHATIF)' } else { '' }))

    if (-not (Test-Path $sourceExe)) {
        Write-Log "ERROR source has no ArgosyUpdater.exe, nothing done"
        exit 2
    }

    # why install is needed ------------------------------------------------------------------------------
    $reasons = @()
    if (-not (Test-Path (Join-Path $target 'ArgosyUpdater.exe'))) { $reasons += 'not installed' }
    foreach ($s in $shortcuts) { if (-not (Test-Path $s)) { $reasons += "missing $s" } }

    if ($reasons.Count -eq 0) {
        # list only, top level like install copies; bit 1 of the exit code = files would be copied, 8+ = failure
        # this script is not compared: changing it must not reinstall every PC (install still copies it along)
        $out = & robocopy.exe $Source $target /L /R:1 /W:1 /NP /NDL /NJH /NJS /XF 'Deploy-ArgosyUpdater.ps1'
        $rc = $LASTEXITCODE
        if ($rc -ge 8) { Write-Log "ERROR robocopy /L exit code $rc"; exit 1 }
        if ($rc -band 1) {
            $reasons += 'changed files on share'
            $out | Where-Object { $_ -match '\S' } | ForEach-Object { Write-Log "  $($_.Trim())" }
        }
    }

    if ($reasons.Count -eq 0) {
        Write-Log ("END up to date, version " + (Get-Item (Join-Path $target 'ArgosyUpdater.exe')).VersionInfo.FileVersion)
        exit 0
    }

    # install ---------------------------------------------------------------------------------------------
    Write-Log ("install needed: " + ($reasons -join '; '))
    if (-not $PSCmdlet.ShouldProcess($sourceExe, 'install')) { exit 0 }

    # GUI exe: Start-Process -Wait to get its exit code, 5 = install successful
    $p = Start-Process -FilePath $sourceExe -ArgumentList 'install' -WorkingDirectory $Source -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 5) {
        Write-Log "ERROR ArgosyUpdater.exe install exit code $($p.ExitCode)"
        exit 1
    }

    Write-Log ("END installed version " + (Get-Item (Join-Path $target 'ArgosyUpdater.exe')).VersionInfo.FileVersion)
    exit 0
}
catch {
    Write-Log "ERROR $($_.Exception.Message)"
    exit 1
}
