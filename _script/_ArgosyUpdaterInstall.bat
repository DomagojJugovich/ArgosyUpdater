REM ArgosyUpdater.exe install

powershell -NoProfile -Command "$p = Start-Process -FilePath .\ArgosyUpdater.exe -ArgumentList install -PassThru -Wait -Verb RunAs; 'Install exit code: ' + $p.ExitCode"

pause