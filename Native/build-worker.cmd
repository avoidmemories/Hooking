@echo off
setlocal
set "OUTDIR=%~1"
set "SRCDIR=%~2"

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

set "VSDEVCMD="
for %%V in (18 17) do (
  for %%E in (Community Professional Enterprise BuildTools) do (
    if not defined VSDEVCMD if exist "C:\Program Files\Microsoft Visual Studio\%%V\%%E\Common7\Tools\VsDevCmd.bat" (
      set "VSDEVCMD=C:\Program Files\Microsoft Visual Studio\%%V\%%E\Common7\Tools\VsDevCmd.bat"
    )
  )
)

if not defined VSDEVCMD (
  echo Could not locate VsDevCmd.bat >&2
  exit /b 1
)

call "%VSDEVCMD%" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b %errorlevel%

rem Keep the intermediate object beside the generated worker, not in the project root.
cl /nologo /std:c++20 /O2 /EHsc /DUNICODE /D_UNICODE /Fo:"%OUTDIR%\SnapHookWorker.obj" /Fe:"%OUTDIR%\SnapHookWorker.exe" "%SRCDIR%\SnapHookWorker.cpp" user32.lib shell32.lib
exit /b %errorlevel%
