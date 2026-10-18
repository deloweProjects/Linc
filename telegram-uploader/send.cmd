@echo off
rem Telegram uploader for Windows. Usage: send.cmd [-Message "text"] file [file...]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0send.ps1" %*
exit /b %ERRORLEVEL%
