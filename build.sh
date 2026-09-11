#!/usr/bin/env bash
set -euo pipefail

# Ensure script is run from repository root
cd "$(dirname "$0")"

# Define directories
BUILD_DIR="build"
PACKAGE_SRC="src/UnityLeanMcp.Unity3d/Packages/com.pereviader.unityleanmcp"

echo "=== Starting UnityLeanMcp build ==="

# 1. Clean and recreate the build directory
if [ -d "$BUILD_DIR" ]; then
  echo "Cleaning existing build directory: $BUILD_DIR..."
  rm -rf "$BUILD_DIR"
fi
mkdir -p "$BUILD_DIR"

# 2. Copy the contents of the package source into the build folder (excluding MCP~)
echo "Copying package contents..."
shopt -s dotglob nullglob
for item in "$PACKAGE_SRC"/*; do
  if [ "$(basename "$item")" != "MCP~" ]; then
    cp -R "$item" "$BUILD_DIR/"
  fi
done
shopt -u dotglob nullglob

# 3. Build and publish the cross-platform .NET MCP Server directly into build MCP~
echo "Publishing UnityLeanMcp.Mcp server to $BUILD_DIR/MCP~..."
mkdir -p "$BUILD_DIR/MCP~"
dotnet publish src/UnityLeanMcp.Mcp/UnityLeanMcp.Mcp.csproj -c Release -f net10.0 -o "$BUILD_DIR/MCP~"

# 4. Update version in package.json from .env.shared
if [ -f ".env.shared" ]; then
  VERSION_VAL=$(source .env.shared && echo "$VERSION")
  echo "Updating version in build/package.json to $VERSION_VAL..."
  if jq --version &> /dev/null; then
    jq --arg ver "$VERSION_VAL" '.version = $ver' "$BUILD_DIR/package.json" > "$BUILD_DIR/package.json.tmp" && mv "$BUILD_DIR/package.json.tmp" "$BUILD_DIR/package.json"
  elif node --version &> /dev/null; then
    node -e "const fs = require('fs'); const p = '$BUILD_DIR/package.json'; const d = JSON.parse(fs.readFileSync(p, 'utf8')); d.version = '$VERSION_VAL'; fs.writeFileSync(p, JSON.stringify(d, null, 2) + '\n', 'utf8');"
  elif python3 --version &> /dev/null; then
    python3 -c "import json; p='$BUILD_DIR/package.json'; d=json.load(open(p)); d['version']='$VERSION_VAL'; json.dump(d, open(p, 'w'), indent=2)"
  elif python --version &> /dev/null; then
    python -c "import json; p='$BUILD_DIR/package.json'; d=json.load(open(p)); d['version']='$VERSION_VAL'; json.dump(d, open(p, 'w'), indent=2)"
  else
    echo "Error: No jq, node, python3, or python found to update package.json version"
    exit 1
  fi
else
  echo "Error: .env.shared not found!"
  exit 1
fi

# 5. Verify published MCP binary exists in build
if [ ! -f "$BUILD_DIR/MCP~/UnityLeanMcp.Mcp.dll" ]; then
  echo "Error: UnityLeanMcp.Mcp.dll was not found in $BUILD_DIR/MCP~/" >&2
  exit 1
fi

# 6. Optionally sync newly built binaries to $PACKAGE_SRC/MCP~ if not locked
echo "Attempting to sync newly built binaries to $PACKAGE_SRC/MCP~..."
mkdir -p "$PACKAGE_SRC/MCP~"
cp -R "$BUILD_DIR/MCP~/." "$PACKAGE_SRC/MCP~/" 2>/dev/null || echo "Notice: Could not sync binaries to $PACKAGE_SRC/MCP~ (files may be in use by a running MCP server). Build output in $BUILD_DIR is intact."

echo "=== Build completed successfully! ==="
