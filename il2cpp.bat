@echo off
set SCRIPT_DIR=%~dp0
mkdir output
"%SCRIPT_DIR%Il2CppInspector.exe" -i libil2cpp.so -m global-metadata.dat --select-outputs -d output/DummyDll -o metadata.json -p il2cpp.py -t IDA
pause