#!/usr/bin/env bash
set -euo pipefail

# Ensure script is run from repository root
cd "$(dirname "$0")"

# Define directories
BUILD_DIR="build"
PACKAGE_SRC="Packages/com.pereviader.unityclirunner"

echo "=== Starting UnityCliRunner build ==="

# 1. Clean and recreate the build directory
if [ -d "$BUILD_DIR" ]; then
  echo "Cleaning existing build directory: $BUILD_DIR..."
  rm -rf "$BUILD_DIR"
fi
mkdir -p "$BUILD_DIR"

# 2. Copy the contents of the package source into the build folder
echo "Copying package contents..."
# cp -R with trailing '/.' copies all contents of source, including hidden files/directories
cp -R "$PACKAGE_SRC/." "$BUILD_DIR/"

# 3. Copy unitycli.sh into the Templates~ folder, overwriting the dummy placeholder
echo "Copying actual unitycli.sh to build templates..."
cp "unitycli.sh" "$BUILD_DIR/Templates~/unitycli.sh"
chmod +x "$BUILD_DIR/Templates~/unitycli.sh"

# 4. Copy unity-cli agent skill into the Templates~ folder, replacing the dummy placeholder
echo "Copying actual unity-cli agent skill to build templates..."
# Remove the dummy placeholder directory
rm -rf "$BUILD_DIR/Templates~/.agents/skills/unity-cli"
# Copy the actual skill directory recursively
cp -R ".agents/skills/unity-cli" "$BUILD_DIR/Templates~/.agents/skills/"

echo "=== Build completed successfully! ==="
