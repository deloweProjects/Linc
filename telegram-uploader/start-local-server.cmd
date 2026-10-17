@echo off
rem Start the local Bot API server (2 GB uploads). Add -Stop to stop it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-local-server.ps1" %*
exit /b %ERRORLEVEL%
