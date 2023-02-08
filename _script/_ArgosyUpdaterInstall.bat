REM ArgosyUpdater.exe install

powershell Start-Process -FilePath .\ArgosyUpdater.exe -ArgumentList install -PassThru -Wait -verb runas 

pause