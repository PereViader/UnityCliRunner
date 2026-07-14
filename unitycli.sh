#!/usr/bin/env bash
set -u

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Find the actual script in Packages or Library/PackageCache
SCRIPT_PATH=""

# 1. Check in Library/PackageCache
# Sort to pick the latest version if multiple exist
candidates=("$PROJECT_ROOT"/Library/PackageCache/com.pereviader.unityclirunner@*/CLI~/unitycli.sh)
if [ ${#candidates[@]} -gt 0 ] && [ -f "${candidates[0]}" ]; then
  SCRIPT_PATH="${candidates[${#candidates[@]}-1]}"
# 2. Check in Packages (local/development package)
elif [ -f "$PROJECT_ROOT/Packages/com.pereviader.unityclirunner/CLI~/unitycli.sh" ]; then
  SCRIPT_PATH="$PROJECT_ROOT/Packages/com.pereviader.unityclirunner/CLI~/unitycli.sh"
# 3. Check Packages/manifest.json for local file references
elif [ -f "$PROJECT_ROOT/Packages/manifest.json" ]; then
  local_path=$(grep -o '"com.pereviader.unityclirunner"[[:space:]]*:[[:space:]]*"file:[^"]*"' "$PROJECT_ROOT/Packages/manifest.json" | sed 's/.*"file:\([^"]*\)".*/\1/')
  if [ -n "$local_path" ]; then
    # Unity resolves "file:" paths relative to the Packages/ folder
    resolved_local_path=$(cd "$PROJECT_ROOT/Packages" && cd "$local_path" 2>/dev/null && pwd)
    if [ -n "$resolved_local_path" ] && [ -f "$resolved_local_path/CLI~/unitycli.sh" ]; then
      SCRIPT_PATH="$resolved_local_path/CLI~/unitycli.sh"
    fi
  fi
fi

if [ -z "$SCRIPT_PATH" ]; then
  echo "Error: Could not find com.pereviader.unityclirunner package script." >&2
  echo "Please make sure the package is installed in your Unity project." >&2
  exit 1
fi

export UNITY_CLI_PROJECT_ROOT="$PROJECT_ROOT"
exec bash "$SCRIPT_PATH" "$@"
