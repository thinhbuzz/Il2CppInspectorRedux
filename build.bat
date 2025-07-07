@echo off
setlocal enabledelayedexpansion

if exist ".\win-x64" rmdir /s /q ".\win-x64"

dotnet restore .\Il2CppInspector.CLI\Il2CppInspector.CLI.csproj -r win-x64
dotnet publish -c Release --no-self-contained --no-restore -o .\win-x64 -r win-x64 .\Il2CppInspector.CLI\Il2CppInspector.CLI.csproj

copy il2cpp.bat .\win-x64\
copy pogo.bat .\win-x64\

mkdir .\win-x64\bun-builder
copy bun-builder\index.ts .\win-x64\bun-builder\
copy bun-builder\bun.lock .\win-x64\bun-builder\
copy bun-builder\package.json .\win-x64\bun-builder\

echo add to path: %CD%\win-x64

rem cd ~/Desktop/projects/pgtools/apks/0.367.2
rem rm -rf output
rem il2cpp
rem ls output/Code/holo-game/Niantic/Platform/NianticInventoryCache* 