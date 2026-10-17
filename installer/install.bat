@echo off
rem octoFixes - close the game, unpack the entire archive, then run this installer.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "INSTALL_RESULT=%ERRORLEVEL%"
echo.
if not "%INSTALL_RESULT%"=="0" (
  echo Installation did NOT complete. See messages above.
) else (
  echo Done. You can close this window.
)
pause
exit /b %INSTALL_RESULT%
