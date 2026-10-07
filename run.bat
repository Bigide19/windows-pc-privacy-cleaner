@echo off
rem Launcher for Clean-PersonalData.ps1
rem Keep this file ASCII-only. cmd.exe reads .bat files in the system code page,
rem so Korean text here would break on some PCs.
cd /d "%~dp0"

if not exist "%~dp0Clean-PersonalData.ps1" (
  echo [ERROR] Clean-PersonalData.ps1 not found.
  echo Extract the whole zip file first, then run run.bat from the extracted folder.
  pause
  exit /b 1
)

rem Call PowerShell by its full path. Some company PCs have a PATH that an installer
rem overwrote, so plain "powershell.exe" is "not recognized" there.
set "PS_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS_EXE%" (
  echo [ERROR] Windows PowerShell not found:
  echo   %PS_EXE%
  echo Take a screenshot of this window and send it.
  pause
  exit /b 1
)

rem -ExecutionPolicy Bypass lets the script run without changing system settings.
rem It cannot override a company policy (GPO/AppLocker). In that case PowerShell
rem prints an error below and this window stays open.
"%PS_EXE%" -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Clean-PersonalData.ps1" %*

if errorlevel 1 (
  echo.
  echo [ERROR] The tool stopped with an error.
  echo Take a screenshot of this window and send it with the log-*.txt file in this folder.
  pause
)
