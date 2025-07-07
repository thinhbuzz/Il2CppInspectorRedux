#!/bin/bash
set -e

SCRIPT_DIR="$(dirname "$0")"
BUN_DIR="$SCRIPT_DIR/bun-builder"
CURRENT_DIR=$(pwd)

# Check if node_modules exists, if not run bun install
if [ ! -d "$BUN_DIR/node_modules" ]; then
    cd "$BUN_DIR" && bun install
    if [ $? -ne 0 ]; then
        exit $?
    fi
    cd $CURRENT_DIR
fi

# Check if Il2CppInspector exists
IL2CPP_INSPECTOR="$SCRIPT_DIR/Il2CppInspector"
if [ ! -f "$IL2CPP_INSPECTOR" ]; then
    echo "Il2CppInspector not found in $SCRIPT_DIR"
    read -p "Press any key to continue..."
    exit 1
fi

# Check if --apkm is already in the arguments
if [[ "$*" == *"--apkm"* ]]; then
    # --apkm already present, use arguments as-is
    bun run "$BUN_DIR/index.ts" "$@"
else
    # --apkm not present, add it
    bun run "$BUN_DIR/index.ts" --apkm "$@"
fi

read -p "Press any key to continue..."
