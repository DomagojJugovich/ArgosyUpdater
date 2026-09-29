REM ArgosyUpdater.exe install
REM must be next to ArgosyUpdater.exe. %~dp0 = folder of this batch (\\server\share\... when started from UNC),
REM not pushd: its temporary drive letter is not visible to the elevated (RunAs) process

powershell -NoProfile -Command "$p = Start-Process -FilePath '%~dp0ArgosyUpdater.exe' -ArgumentList install -PassThru -Wait -Verb RunAs; 'Install exit code: ' + $p.ExitCode"

pause
