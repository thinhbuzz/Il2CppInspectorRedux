@ECHO OFF
SET SCRIPT_DIR=%~dp0
SET BUN_DIR=%SCRIPT_DIR%bun-builder
SET CURRENT_DIR=%CD%
IF NOT EXIST %BUN_DIR%/node_modules (
    cd /d %BUN_DIR% && bun install
    IF %ERRORLEVEL% NEQ 0 EXIT /B %ERRORLEVEL%
    cd /d %CURRENT_DIR%
)
SET IL2CPP_INSPECTOR=%SCRIPT_DIR%Il2CppInspector.exe
IF NOT EXIST %IL2CPP_INSPECTOR% (
    echo Il2CppInspector.exe not found in %SCRIPT_DIR%
    PAUSE
    EXIT /B 1
)
REM Check if --apkm is already in the arguments
set HAS_APKM_ARG=false

:: Loop through all arguments
for %%A in (%*) do (
    if "%%A"=="--apkm" set HAS_APKM_ARG=true
)

:: Check if --apkm was found
if "%HAS_APKM_ARG%"=="true" (
    bun run %BUN_DIR%/index.ts %*
) else (
    bun run %BUN_DIR%/index.ts --apkm %*
)

IF %ERRORLEVEL% NEQ 0 EXIT /B %ERRORLEVEL%
