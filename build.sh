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

# 2.5. Update version in package.json from .env.shared
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


# 3. Copy unitycli.sh (wrapper) into the CLI~ folder as unitycli-forward.sh
echo "Copying forwarding script to build CLI~ folder..."
cp "unitycli.sh" "$BUILD_DIR/CLI~/unitycli-forward.sh"
chmod +x "$BUILD_DIR/CLI~/unitycli-forward.sh"

# 4. Copy unity-cli agent skill into the CLI~ folder, replacing the dummy placeholder
echo "Copying actual unity-cli agent skill to build CLI~ folder..."
# Remove the dummy placeholder directory
rm -rf "$BUILD_DIR/CLI~/.agents/skills/unity-cli"
# Copy the actual skill directory recursively
cp -R ".agents/skills/unity-cli" "$BUILD_DIR/CLI~/.agents/skills/"

echo "=== Build completed successfully! ==="
