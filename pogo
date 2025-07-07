#!/bin/bash
set -e

# Check if correct number of arguments provided
if [ $# -lt 1 ] || [ $# -gt 3 ]; then
    echo "Usage: pogo '/path/to/file.zip' [file1.apk] [file2.apk]"
    echo "Extracts:"
    echo "  - assets/bin/Data/Managed/Metadata/global-metadata.dat from file1.apk (default: base.apk)"
    echo "  - lib/arm64-v8a/libil2cpp.so from file2.apk (default: split_config.arm64_v8a.apk)"
    echo "Both files are extracted to the same directory as the ZIP file"
    exit 1
fi

ZIP_FILE="$1"
APK1="${2:-base.apk}"
APK2="${3:-split_config.arm64_v8a.apk}"

# Check if ZIP file exists
if [ ! -f "$ZIP_FILE" ]; then
    echo "Error: ZIP file '$ZIP_FILE' not found"
    exit 1
fi

# Get the directory where the ZIP file is located
ZIP_DIR=$(dirname "$ZIP_FILE")
ZIP_BASENAME=$(basename "$ZIP_FILE" .zip)

# Create temporary directory
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

echo "Extracting APK files from ZIP..."

# Extract the ZIP file to temp directory
unzip -q "$ZIP_FILE" -d "$TEMP_DIR"

# Check if APK files exist in the extracted content, with fallbacks
if [ ! -f "$TEMP_DIR/$APK1" ]; then
    # Try fallback for APK1 if using default
    if [ "$APK1" = "base.apk" ] && [ -f "$TEMP_DIR/com.nianticlabs.pokemongo.apk" ]; then
        APK1="com.nianticlabs.pokemongo.apk"
        echo "Using fallback APK1: $APK1"
    else
        echo "Error: APK file '$APK1' not found in ZIP archive"
        exit 1
    fi
fi

if [ ! -f "$TEMP_DIR/$APK2" ]; then
    # Try fallback for APK2 if using default
    if [ "$APK2" = "split_config.arm64_v8a.apk" ] && [ -f "$TEMP_DIR/config.arm64_v8a.apk" ]; then
        APK2="config.arm64_v8a.apk"
        echo "Using fallback APK2: $APK2"
    else
        echo "Error: APK file '$APK2' not found in ZIP archive"
        exit 1
    fi
fi

echo "Extracting global-metadata.dat from $APK1..."

# Extract global-metadata.dat from first APK
APK1_TEMP="$TEMP_DIR/apk1_extracted"
mkdir -p "$APK1_TEMP"
unzip -q "$TEMP_DIR/$APK1" -d "$APK1_TEMP"

METADATA_FILE="$APK1_TEMP/assets/bin/Data/Managed/Metadata/global-metadata.dat"
if [ ! -f "$METADATA_FILE" ]; then
    echo "Error: global-metadata.dat not found in $APK1"
    exit 1
fi

# Copy metadata file to output directory
cp "$METADATA_FILE" "$ZIP_DIR/global-metadata.dat"
echo "Extracted global-metadata.dat to $ZIP_DIR/global-metadata.dat"

echo "Extracting libil2cpp.so from $APK2..."

# Extract libil2cpp.so from second APK
APK2_TEMP="$TEMP_DIR/apk2_extracted"
mkdir -p "$APK2_TEMP"
unzip -q "$TEMP_DIR/$APK2" -d "$APK2_TEMP"

LIBIL2CPP_FILE="$APK2_TEMP/lib/arm64-v8a/libil2cpp.so"
if [ ! -f "$LIBIL2CPP_FILE" ]; then
    echo "Error: libil2cpp.so not found in $APK2"
    exit 1
fi

# Copy libil2cpp.so file to output directory
cp "$LIBIL2CPP_FILE" "$ZIP_DIR/libil2cpp.so"
echo "Extracted libil2cpp.so to $ZIP_DIR/libil2cpp.so"

echo "Extraction completed successfully!"
echo "Files extracted to: $ZIP_DIR"

echo "Running il2cpp..."
cd $ZIP_DIR && il2cpp

echo "Done!"
