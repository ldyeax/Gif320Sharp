@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"
if "%DOTNET_FRAMEWORK%"=="" set "DOTNET_FRAMEWORK=net9.0"
if "%GIMP_VERSION%"=="" set "GIMP_VERSION=3.0"
if "%MSYS_BIN%"=="" set "MSYS_BIN=C:\tools\msys64\mingw64\bin"
if "%GIMP_ROOT%"=="" set "GIMP_ROOT=%ProgramFiles%\GIMP 3"

set "RESTORE_ARG=--no-restore"
if "%RESTORE%"=="1" set "RESTORE_ARG="

echo Installing Gif320Sharp GIMP plug-in from:
echo   %REPO_ROOT%

if not "%ALLOW_RUNNING_GIMP%"=="1" (
	powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Get-Process | Where-Object { $_.ProcessName -like 'gimp*' -or $_.ProcessName -like 'gegl*' }; if ($p) { $p | ForEach-Object { Write-Host ($_.ProcessName + ' is running. Close GIMP/GEGL before replacing the installed modules.') }; exit 1 }"
	if errorlevel 1 (
		echo Set ALLOW_RUNNING_GIMP=1 to bypass this check.
		exit /b 1
	)
)

if not "%SKIP_DOTNET_BUILD%"=="1" (
	echo Building managed CLI...
	dotnet build "%REPO_ROOT%\Gif320Sharp.slnx" --configuration "%CONFIGURATION%" --framework "%DOTNET_FRAMEWORK%" %RESTORE_ARG%
	if errorlevel 1 exit /b 1
)

set "CLI_BUILD_DIR=%GIF320SHARP_BUILD_DIR%"
if "%CLI_BUILD_DIR%"=="" set "CLI_BUILD_DIR=%REPO_ROOT%\Gif320Sharp\bin\%CONFIGURATION%\%DOTNET_FRAMEWORK%"
if not exist "%CLI_BUILD_DIR%\gif320sharp.exe" (
	echo Missing built CLI: "%CLI_BUILD_DIR%\gif320sharp.exe"
	exit /b 1
)

set "GEGL_BUILD_DIR=%REPO_ROOT%\Gif320Sharp_Gegl\build"
if not "%SKIP_GEGL_BUILD%"=="1" (
	set "MESON_EXE=%MESON%"
	if not defined MESON_EXE if exist "%MSYS_BIN%\meson.exe" set "MESON_EXE=%MSYS_BIN%\meson.exe"
	if not defined MESON_EXE (
		for /f "delims=" %%M in ('where meson.exe 2^>NUL') do if not defined MESON_EXE set "MESON_EXE=%%M"
	)
	if not defined MESON_EXE (
		echo Meson was not found. Set MESON to meson.exe or set SKIP_GEGL_BUILD=1 to use existing GEGL DLLs.
		exit /b 1
	)
	if exist "%MSYS_BIN%" set "PATH=%MSYS_BIN%;!PATH!"
	if exist "%GIMP_ROOT%\bin" set "PATH=%MSYS_BIN%;%GIMP_ROOT%\bin;!PATH!"
	if not defined PKG_CONFIG_PATH if exist "%GIMP_ROOT%\lib\pkgconfig" set "PKG_CONFIG_PATH=%GIMP_ROOT%\lib\pkgconfig"
	if not exist "%GEGL_BUILD_DIR%\build.ninja" (
		echo Configuring GEGL modules...
		"!MESON_EXE!" setup "%GEGL_BUILD_DIR%" "%REPO_ROOT%\Gif320Sharp_Gegl"
		if errorlevel 1 exit /b 1
	)
	echo Building GEGL modules...
	"!MESON_EXE!" compile -C "%GEGL_BUILD_DIR%"
	if errorlevel 1 exit /b 1
)

set "PLUGIN_DIR=%GIMP_PLUGIN_DIR%"
if "%PLUGIN_DIR%"=="" set "PLUGIN_DIR=%APPDATA%\GIMP\%GIMP_VERSION%\plug-ins\gif320sharp_export"
set "PLUGIN_BIN_DIR=%PLUGIN_DIR%\bin"
mkdir "%PLUGIN_BIN_DIR%" >NUL 2>NUL

echo Installing Python plug-in...
copy /Y "%REPO_ROOT%\Gif320Sharp_Gimp\gif320sharp_export.py" "%PLUGIN_DIR%\gif320sharp_export.py" >NUL
if errorlevel 1 exit /b 1

echo Installing bundled CLI...
for %%F in (gif320sharp.exe gif320sharp.dll Gif320Sharp_Core.dll gif320sharp.deps.json gif320sharp.runtimeconfig.json) do (
	if not exist "%CLI_BUILD_DIR%\%%F" (
		echo Missing runtime file: "%CLI_BUILD_DIR%\%%F"
		exit /b 1
	)
	copy /Y "%CLI_BUILD_DIR%\%%F" "%PLUGIN_BIN_DIR%\%%F" >NUL
	if errorlevel 1 exit /b 1
)

if "%GEGL_PLUGIN_DIR%"=="" (
	if "%GIMP_PREFIX%"=="" set "GIMP_PREFIX=%ProgramFiles%\GIMP 3"
	if exist "!GIMP_PREFIX!\lib\gegl-0.4" (
		set "GEGL_PLUGIN_DIR=!GIMP_PREFIX!\lib\gegl-0.4"
	) else (
		set "GEGL_PLUGIN_DIR=%LOCALAPPDATA%\gegl-0.4\plug-ins"
	)
)

if not exist "%GEGL_BUILD_DIR%\libgif320sharp-vt320-preview.dll" (
	echo Missing GEGL module: "%GEGL_BUILD_DIR%\libgif320sharp-vt320-preview.dll"
	exit /b 1
)
if not exist "%GEGL_BUILD_DIR%\libgif320sharp-vt320-second-pass.dll" (
	echo Missing GEGL module: "%GEGL_BUILD_DIR%\libgif320sharp-vt320-second-pass.dll"
	exit /b 1
)

mkdir "%GEGL_PLUGIN_DIR%" >NUL 2>NUL
echo Installing GEGL modules to:
echo   %GEGL_PLUGIN_DIR%
copy /Y "%GEGL_BUILD_DIR%\libgif320sharp-vt320-preview.dll" "%GEGL_PLUGIN_DIR%\libgif320sharp-vt320-preview.dll" >NUL
if errorlevel 1 exit /b 1
copy /Y "%GEGL_BUILD_DIR%\libgif320sharp-vt320-second-pass.dll" "%GEGL_PLUGIN_DIR%\libgif320sharp-vt320-second-pass.dll" >NUL
if errorlevel 1 exit /b 1

set "USER_GEGL_DIR=%LOCALAPPDATA%\gegl-0.4\plug-ins"
if /I not "%GEGL_PLUGIN_DIR%"=="%USER_GEGL_DIR%" (
	del /Q "%USER_GEGL_DIR%\libgif320sharp-vt320-preview.dll" >NUL 2>NUL
	del /Q "%USER_GEGL_DIR%\libgif320sharp-vt320-second-pass.dll" >NUL 2>NUL
)

echo.
echo Installed Gif320Sharp GIMP plug-in.
echo Restart GIMP to load the updated plug-in and GEGL modules.
