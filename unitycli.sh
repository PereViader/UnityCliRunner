#!/usr/bin/env bash
set -u

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

find_unity_path() {
  if [ -n "${UNITY_PATH:-}" ] && [ -f "$UNITY_PATH" ]; then
    echo "$UNITY_PATH" | tr -d '\r'
    return 0
  fi

  if [ "${USE_UNITY_EDITOR_WRAPPER:-false}" = "true" ]; then
    local container_unity=""
    container_unity=$(command -v unity-editor 2>/dev/null | tr -d '\r')
    if [ -n "$container_unity" ]; then
      echo "$container_unity"
      return 0
    fi
  fi

  local version=""
  if [ -f "$PROJECT_ROOT/ProjectSettings/ProjectVersion.txt" ]; then
    version=$(grep "m_EditorVersion:" "$PROJECT_ROOT/ProjectSettings/ProjectVersion.txt" | awk '{print $2}' | tr -d '\r')
  fi

  local is_windows=false
  if [[ "${OSTYPE:-}" == "msys" || "${OSTYPE:-}" == "cygwin" || "${OSTYPE:-}" == "mingw"* || "${OS:-}" == "Windows_NT" ]]; then
    is_windows=true
  fi

  local paths=()
  if [ "$is_windows" = true ] && [ -n "$version" ]; then
    paths=(
      "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
      "C:/Program Files (x86)/Unity/Hub/Editor/$version/Editor/Unity.exe"
      "C:/Unity/Hub/Editor/$version/Editor/Unity.exe"
      "D:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
      "D:/Unity/Hub/Editor/$version/Editor/Unity.exe"
    )
  elif [[ "$(uname)" == "Darwin" ]] && [ -n "$version" ]; then
    paths=(
      "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
    )
  elif [ -n "$version" ]; then
    paths=(
      "$HOME/Unity/Hub/Editor/$version/Editor/Unity"
      "/opt/unity/Editor/Unity"
      "/opt/Unity/Editor/Unity"
    )
  fi

  for p in "${paths[@]}"; do
    if [ -f "$p" ]; then
      echo "$p"
      return 0
    fi
  done

  local command_unity=""
  if [ "$is_windows" = true ]; then
    command_unity=$(where unity 2>/dev/null | head -n 1 | tr -d '\r')
    if [ -z "$command_unity" ]; then
      for cmd in Unity.exe unity.exe Unity unity; do
        command_unity=$(command -v "$cmd" 2>/dev/null | tr -d '\r')
        if [ -n "$command_unity" ]; then
          break
        fi
      done
    fi
  else
    for cmd in unity-editor Unity unity; do
      command_unity=$(command -v "$cmd" 2>/dev/null)
      if [ -n "$command_unity" ]; then
        break
      fi
    done
  fi

  if [ -n "$command_unity" ]; then
    echo "$command_unity"
    return 0
  fi

  return 1
}

# Find the actual script in Packages or Library/PackageCache
SCRIPT_PATH=""

find_script_path() {
  SCRIPT_PATH=""
  # 1. Check in Library/PackageCache
  # Sort to pick the latest version if multiple exist
  local candidates=("$PROJECT_ROOT"/Library/PackageCache/com.pereviader.unityclirunner@*/CLI~/unitycli.sh)
  if [ ${#candidates[@]} -gt 0 ] && [ -f "${candidates[0]}" ]; then
    SCRIPT_PATH="${candidates[${#candidates[@]}-1]}"
  # 2. Check in Packages (local/development package)
  elif [ -f "$PROJECT_ROOT/Packages/com.pereviader.unityclirunner/CLI~/unitycli.sh" ]; then
    SCRIPT_PATH="$PROJECT_ROOT/Packages/com.pereviader.unityclirunner/CLI~/unitycli.sh"
  # 3. Check Packages/manifest.json for local file references
  elif [ -f "$PROJECT_ROOT/Packages/manifest.json" ]; then
    local local_path
    local_path=$(grep -o '"com.pereviader.unityclirunner"[[:space:]]*:[[:space:]]*"file:[^"]*"' "$PROJECT_ROOT/Packages/manifest.json" | sed 's/.*"file:\([^"]*\)".*/\1/')
    if [ -n "$local_path" ]; then
      # Unity resolves "file:" paths relative to the Packages/ folder
      local resolved_local_path
      resolved_local_path=$(cd "$PROJECT_ROOT/Packages" && cd "$local_path" 2>/dev/null && pwd)
      if [ -n "$resolved_local_path" ] && [ -f "$resolved_local_path/CLI~/unitycli.sh" ]; then
        SCRIPT_PATH="$resolved_local_path/CLI~/unitycli.sh"
      fi
    fi
  fi
}

find_script_path

if [ -z "$SCRIPT_PATH" ] || [ ! -f "$SCRIPT_PATH" ]; then
  echo "com.pereviader.unityclirunner not found in Library/PackageCache. Initializing Unity project to resolve packages..."
  UNITY_EXE=$(find_unity_path || true)
  if [ -n "$UNITY_EXE" ]; then
    mkdir -p "$PROJECT_ROOT/Temp"
    "$UNITY_EXE" -batchmode -nographics -projectPath "$PROJECT_ROOT" -logFile "$PROJECT_ROOT/Temp/unity_package_resolve.log" -quit >/dev/null 2>&1 || true
    find_script_path
  else
    echo "Warning: Could not locate Unity executable to initialize packages automatically." >&2
  fi
fi

if [ -z "$SCRIPT_PATH" ] || [ ! -f "$SCRIPT_PATH" ]; then
  echo "Error: Could not find com.pereviader.unityclirunner package script in Library/PackageCache, Packages, or manifest.json references." >&2
  if [ -f "$PROJECT_ROOT/Temp/unity_package_resolve.log" ]; then
    echo "Check '$PROJECT_ROOT/Temp/unity_package_resolve.log' for details on why Unity failed to resolve packages." >&2
  fi
  echo "Please make sure the package is installed in your Unity project." >&2
  exit 1
fi

chmod +x "$SCRIPT_PATH" 2>/dev/null || true
export UNITY_CLI_PROJECT_ROOT="$PROJECT_ROOT"
exec bash "$SCRIPT_PATH" "$@"
