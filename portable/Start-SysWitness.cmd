@echo off
setlocal
pushd "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-SysWitness.ps1"
set "EXITCODE=%ERRORLEVEL%"
popd
if not "%EXITCODE%"=="0" (
  echo.
  echo SysWitness could not start. Read the error above.
  pause
)
exit /b %EXITCODE%
