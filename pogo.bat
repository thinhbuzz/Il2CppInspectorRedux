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
SET APKM_FLAG=--apkm
SET ARGS=%*
ECHO %ARGS% | FIND /I "%APKM_FLAG%" >nul

IF %ERRORLEVEL% EQU 0 (
    REM --apkm already present, use arguments as-is
    SET ERRORLEVEL=0
    bun run %BUN_DIR%index.ts %*
) ELSE (
    REM --apkm not present, add it
    SET ERRORLEVEL=0
    bun run %BUN_DIR%index.ts --apkm %*
)

IF %ERRORLEVEL% NEQ 0 EXIT /B %ERRORLEVEL%
PAUSE
