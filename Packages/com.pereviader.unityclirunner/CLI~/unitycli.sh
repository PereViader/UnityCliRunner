#!/usr/bin/env bash

# Exit immediately if a command exits with a non-zero status,
# but we handle Unity exit codes manually.
set -u

# Change working directory to the project root
if [ -n "${UNITY_CLI_PROJECT_ROOT:-}" ]; then
  cd "$UNITY_CLI_PROJECT_ROOT" || exit 1
fi

# Detect the host once. The script is always run by Bash, including on
# Windows through Git Bash/MSYS/Cygwin, so uname is more reliable here than
# mutable environment variables.
detect_platform() {
  case "$(uname -s 2>/dev/null || true)" in
    Darwin)
      echo "macos"
      ;;
    Linux)
      echo "linux"
      ;;
    MINGW*|MSYS*|CYGWIN*)
      echo "windows"
      ;;
    *)
      echo "unknown"
      ;;
  esac
}

PLATFORM="$(detect_platform)"
if [ "$PLATFORM" = "unknown" ]; then
  echo "Error: Unsupported host platform. Run unitycli.sh from Bash on Linux, macOS, or Windows Git Bash." >&2
  exit 1
fi

is_windows_platform() {
  [ "$PLATFORM" = "windows" ]
}

# Convert paths between the POSIX view used by Bash and the native Windows
# view used by Win32 programs. On Unix this is intentionally a no-op.
to_shell_path() {
  local path="$1"
  if is_windows_platform && command -v cygpath >/dev/null 2>&1; then
    cygpath -u "$path" 2>/dev/null || printf '%s\n' "$path"
  else
    printf '%s\n' "$path"
  fi
}

to_native_path() {
  local path="$1"
  if is_windows_platform && command -v cygpath >/dev/null 2>&1; then
    cygpath -m -a "$path" 2>/dev/null || printf '%s\n' "$path"
  else
    printf '%s\n' "$path"
  fi
}

PROJECT_PATH="$(pwd)"
PROJECT_NATIVE_PATH="$(to_native_path "$PROJECT_PATH")"
export UNITY_CLI_PROJECT_NATIVE="$PROJECT_NATIVE_PATH"

find_powershell() {
  local command_name
  for command_name in powershell.exe powershell pwsh.exe pwsh; do
    if command -v "$command_name" >/dev/null 2>&1; then
      command -v "$command_name"
      return 0
    fi
  done
  return 1
}

decode_base64() {
  local decoder
  if ! command -v base64 >/dev/null 2>&1; then
    return 1
  fi

  if base64 -d </dev/null >/dev/null 2>&1; then
    decoder="-d"
  elif base64 -D </dev/null >/dev/null 2>&1; then
    decoder="-D"
  else
    return 1
  fi

  base64 "$decoder"
}

# Cleanup background tail on exit
tail_pid=""
cleanup() {
  if [ -n "$tail_pid" ]; then
    kill "$tail_pid" 2>/dev/null
  fi
}
trap cleanup EXIT INT TERM

# Default options
SUBCOMMAND=""
MODE_PLAYMODE=false
MODE_EDITMODE=false
FILTER=""
CATEGORY=""
EXECUTE_METHOD=""
EXECUTE_METHOD_PARAMS=()
EVAL_CODE=""
BG_ACTION=""
BG_MODE=""

# Helper for usage
show_usage() {
  local exit_code="${1:-1}"
  echo "Usage: $0 <command> [options]"
  echo "Commands:"
  echo "  start <mode>            Start a background Unity instance (mode: batchmode | interactive)"
  echo "  stop                    Stop the background Unity instance"
  echo "  status                  Check status of the background Unity instance"
  echo "  refresh                 Trigger AssetDatabase refresh and print compiler diagnostics"
  echo "  recompile               Force a full C# recompilation and print compiler diagnostics"
  echo "  test [options]          Run tests (defaults to running both EditMode and PlayMode)"
  echo "    --playmode            Run PlayMode tests"
  echo "    --editmode            Run EditMode tests"
  echo "    --filter <filter>     Filter tests by name (regex/substring)"
  echo "    --category <category> Filter tests by category"
  echo "  executemethod <method> [args...] Execute a custom static method (optionally with parameters)"
  echo "                          (e.g., Namespace.Class.Method 4 3 \"{\\\"Value\\\":4}\")"
  echo "  eval <code>             Evaluate a live C# expression or script dynamically"
  echo "                          (e.g., \"Application.unityVersion\" or \"1 + 1\")"
  echo "  -h, --help              Show this help message"
  exit "$exit_code"
}

if [ $# -eq 0 ]; then
  show_usage
fi

SUBCOMMAND="$1"
shift

case "$SUBCOMMAND" in
  refresh)
    if [ $# -gt 0 ]; then
      echo "Error: refresh does not accept extra arguments"
      show_usage
    fi
    ;;

  recompile)
    if [ $# -gt 0 ]; then
      echo "Error: recompile does not accept extra arguments"
      show_usage
    fi
    ;;

  test)
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --playmode)
          MODE_PLAYMODE=true
          shift
          ;;
        --editmode)
          MODE_EDITMODE=true
          shift
          ;;
        --filter)
          if [ -z "${2:-}" ]; then
            echo "Error: --filter requires an argument"
            show_usage
          fi
          FILTER="$2"
          shift 2
          ;;
        --filter=*)
          FILTER="${1#*=}"
          shift
          ;;
        --category)
          if [ -z "${2:-}" ]; then
            echo "Error: --category requires an argument"
            show_usage
          fi
          CATEGORY="$2"
          shift 2
          ;;
        --category=*)
          CATEGORY="${1#*=}"
          shift
          ;;
        -h|--help)
          show_usage 0
          ;;
        *)
          echo "Unknown option for test subcommand: $1"
          show_usage
          ;;
      esac
    done

    # If neither mode is specified, default to running both
    if [ "$MODE_PLAYMODE" = false ] && [ "$MODE_EDITMODE" = false ]; then
      MODE_PLAYMODE=true
      MODE_EDITMODE=true
    fi
    ;;

  executemethod)
    if [ $# -eq 0 ]; then
      echo "Error: executemethod requires a method name argument (e.g., Namespace.Class.Method)"
      show_usage
    fi
    EXECUTE_METHOD="$1"
    shift
    EXECUTE_METHOD_PARAMS=("$@")
    shift $#
    ;;

  eval)
    if [ $# -eq 0 ]; then
      echo "Error: eval requires a C# code snippet or expression (e.g., Application.unityVersion)"
      show_usage
    fi
    EVAL_CODE="$*"
    shift $#
    ;;

  start)
    if [ $# -eq 0 ]; then
      echo "Error: start command requires a mode (batchmode|interactive)"
      show_usage
    fi
    BG_MODE="$1"
    if [ "$BG_MODE" != "batchmode" ] && [ "$BG_MODE" != "interactive" ]; then
      echo "Error: start command mode must be batchmode or interactive"
      show_usage
    fi
    shift
    if [ $# -gt 0 ]; then
      echo "Error: start command does not accept extra arguments"
      show_usage
    fi
    ;;

  stop)
    if [ $# -gt 0 ]; then
      echo "Error: stop command does not accept extra arguments"
      show_usage
    fi
    ;;

  status)
    if [ $# -gt 0 ]; then
      echo "Error: status command does not accept extra arguments"
      show_usage
    fi
    ;;

  -h|--help|help)
    show_usage 0
    ;;

  *)
    echo "Unknown command: $SUBCOMMAND"
    show_usage
    ;;
esac

# Function to send a command to the socket server
send_socket_cmd() {
  local cmd="$1"
  local timeout="${2:-10}"

  # Read dynamic port
  local port=""
  if [ -f "Temp/unity_cli_port.txt" ]; then
    port=$(cat "Temp/unity_cli_port.txt" | tr -d '[:space:]')
  fi

  if [ -z "$port" ] || ! [[ "$port" =~ ^[0-9]+$ ]] || [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
    return 1
  fi

  local response=""
  local socket_exit_code=0
  # Bash implements /dev/tcp on Linux, macOS, and Git Bash. Do not probe the
  # port first: that creates a second connection and races with domain reloads.
  # The surrounding redirection is important because Bash can otherwise print
  # connection-refused diagnostics before the command-level redirection runs.
  response=$(
    {
      exec 3<>"/dev/tcp/127.0.0.1/$port" || exit 1
      printf '%s\n' "$cmd" >&3 || exit 1
      IFS= read -r -t "$timeout" line <&3 || exit 1
      printf '%s\n' "$line"
    } 2>/dev/null
  ) || socket_exit_code=$?

  if [ "$socket_exit_code" -ne 0 ] && is_windows_platform; then
    # PowerShell is retained only as a Windows fallback for environments where
    # the Git Bash /dev/tcp extension cannot connect to a native Win32 socket.
    local powershell_command=""
    powershell_command=$(find_powershell || true)
    if [ -n "$powershell_command" ]; then
      socket_exit_code=0
      local timeout_ms=$((timeout * 1000))
      export UNITY_CLI_CMD="$cmd"
      response=$("$powershell_command" -NoProfile -Command "
        \$ErrorActionPreference = 'Stop';
        try {
          \$c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $port);
          \$c.ReceiveTimeout = $timeout_ms;
          \$w = New-Object System.IO.StreamWriter(\$c.GetStream());
          \$r = New-Object System.IO.StreamReader(\$c.GetStream());
          \$w.WriteLine(\$env:UNITY_CLI_CMD);
          \$w.Flush();
          \$res = \$r.ReadLine();
          \$c.Close();
          Write-Output \$res;
        } catch {
          Write-Error \$_.Exception.GetType().Name;
          exit 1;
        }
      " 2>&1) || socket_exit_code=$?
      unset UNITY_CLI_CMD
    fi
  fi

  if [ "$socket_exit_code" -ne 0 ]; then
    local lower_resp
    lower_resp=$(echo "$response" | tr '[:upper:]' '[:lower:]')
    if [[ "$lower_resp" == *"operation not permitted"* || "$lower_resp" == *"forbidden by its access permissions"* || "$lower_resp" == *"permissiondenied"* || "$lower_resp" == *"unauthorizedaccessexception"* ]]; then
      return 42
    fi

    return 1
  fi

  if [ -z "$response" ]; then
    return 1
  fi

  # Strip carriage returns and trim whitespace
  response=$(echo "$response" | tr -d '\r')
  response="${response#"${response%%[![:space:]]*}"}"
  response="${response%"${response##*[![:space:]]}"}"
  echo "$response"
  return 0
}

print_network_permission_error() {
  echo "Error: Local network permission is required to connect to UnityCliRunner at 127.0.0.1. If you are running in a sandbox, allow network access and retry." >&2
}

# Check whether a process is alive. Bash can inspect POSIX processes directly;
# tasklist is the native Windows fallback. PowerShell is used only if that
# fallback is unavailable or cannot answer the query.
is_process_alive() {
  local pid="$1"
  if [ -z "$pid" ] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then
    return 1
  fi

  if kill -0 "$pid" 2>/dev/null; then
    return 0
  fi

  if is_windows_platform; then
    local tasklist_command=""
    local tasklist_name
    for tasklist_name in tasklist.exe tasklist; do
      if command -v "$tasklist_name" >/dev/null 2>&1; then
        tasklist_command=$(command -v "$tasklist_name")
        break
      fi
    done
    if [ -n "$tasklist_command" ]; then
      local tasklist_output=""
      local tasklist_status=0
      tasklist_output=$("$tasklist_command" //FI "PID eq $pid" //FO CSV //NH 2>/dev/null) || tasklist_status=$?
      if [ "$tasklist_status" -eq 0 ]; then
        if printf '%s\n' "$tasklist_output" | grep -q -F "\"$pid\""; then
          return 0
        fi
        return 1
      fi
    fi

    local powershell_command=""
    powershell_command=$(find_powershell || true)
    if [ -n "$powershell_command" ]; then
      "$powershell_command" -NoProfile -Command "
        \$process = Get-Process -Id $pid -ErrorAction SilentlyContinue;
        if (\$null -ne \$process) { exit 0 } else { exit 1 }
      " >/dev/null 2>&1
      return $?
    fi
  fi

  return 1
}

# Find the Unity lockfile used by this project.
get_unity_lockfile() {
  if [ -f "Temp/UnityLockfile" ]; then
    echo "Temp/UnityLockfile"
  elif [ -f "Temp/UnityLockFile" ]; then
    echo "Temp/UnityLockFile"
  fi
}

# Return a live PID associated with the project lockfile, if one can be found.
# UnityLockfile is zero bytes on macOS, so the PID must be obtained from the
# file owner rather than from the file contents on that platform.
get_unity_lock_owner_pid() {
  local lockfile="$1"
  local pid=""
  local filesize=""

  if [ ! -f "$lockfile" ]; then
    return 1
  fi

  filesize=$(wc -c < "$lockfile" 2>/dev/null | tr -d '[:space:]')
  if [ "$filesize" = "4" ] && command -v od >/dev/null 2>&1; then
    pid=$(od -An -t d4 -N 4 "$lockfile" 2>/dev/null | tr -d '[:space:]')
  fi

  if [ -z "$pid" ] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then
    pid=$(cat "$lockfile" 2>/dev/null | tr -d '\r')
    pid="${pid#"${pid%%[![:space:]]*}"}"
    pid="${pid%"${pid##*[![:space:]]}"}"
  fi

  if [[ "$pid" =~ ^[0-9]+$ ]] && is_process_alive "$pid"; then
    echo "$pid"
    return 0
  fi

  # lsof -t emits only PIDs. Unlike fuser's macOS behaviour, its exit status
  # is non-zero when the file has no owner.
  if command -v lsof >/dev/null 2>&1; then
    pid=$(lsof -nP -t "$lockfile" 2>/dev/null | head -n 1)
    if [[ "$pid" =~ ^[0-9]+$ ]] && is_process_alive "$pid"; then
      echo "$pid"
      return 0
    fi
  fi

  # Keep fuser as a Linux fallback, but inspect its output. On macOS it
  # returns success for an unowned file and prints only "file:".
  if command -v fuser >/dev/null 2>&1; then
    local fuser_output=""
    local candidate=""
    fuser_output=$(fuser "$lockfile" 2>&1 || true)
    if [[ "$fuser_output" == *:* ]]; then
      fuser_output="${fuser_output#*:}"
    fi
    for candidate in $fuser_output; do
      if [[ "$candidate" =~ ^[0-9]+$ ]] && is_process_alive "$candidate"; then
        echo "$candidate"
        return 0
      fi
    done
  fi

  return 1
}

remove_stale_unity_lockfiles() {
  rm -f Temp/UnityLockfile Temp/UnityLockFile 2>/dev/null
}

is_unity_socket_ready() {
  local timeout="${1:-1}"
  local response=""
  response=$(send_socket_cmd "PING" "$timeout" 2>/dev/null) || return 1
  [ "$response" = "PONG" ]
}

# Kill exactly the supplied process. Never fall back to killing Unity by image
# name because that can terminate unrelated projects/editors.
kill_process() {
  local pid="$1"
  if [ -z "$pid" ] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then
    return 1
  fi

  if is_windows_platform; then
    if command -v taskkill.exe >/dev/null 2>&1; then
      taskkill.exe //PID "$pid" //F >/dev/null 2>&1
      return $?
    elif command -v taskkill >/dev/null 2>&1; then
      taskkill //PID "$pid" //F >/dev/null 2>&1
      return $?
    fi
  fi

  kill -9 "$pid" >/dev/null 2>&1
}

# Find the Unity process for this project on Windows. Unity's lockfile is
# opened with sharing restrictions, so querying the command line is the only
# reliable fallback when the socket and our own PID file are unavailable.
find_windows_unity_pid() {
  if ! is_windows_platform; then
    return 1
  fi

  local powershell_command=""
  powershell_command=$(find_powershell || true)
  if [ -z "$powershell_command" ]; then
    return 1
  fi

  "$powershell_command" -NoProfile -Command "
    \$project = [string]\$env:UNITY_CLI_PROJECT_NATIVE;
    if ([string]::IsNullOrEmpty(\$project)) { exit 1 }
    \$project = \$project.Replace('/', '\').TrimEnd('\');
    \$process = Get-CimInstance Win32_Process -Filter \"Name = 'Unity.exe'\" |
      Where-Object {
        \$commandLine = [string]\$_.CommandLine;
        if ([string]::IsNullOrEmpty(\$commandLine)) { return \$false }
        \$normalizedCommand = \$commandLine.Replace('/', '\');
        return \$normalizedCommand.IndexOf(\$project, [StringComparison]::OrdinalIgnoreCase) -ge 0;
      } | Select-Object -First 1;
    if (\$null -ne \$process) { \$process.ProcessId }
  " 2>/dev/null | tr -d '\r'
}

# Function to check if Unity is still running.
is_unity_still_running() {
  # Socket readiness is checked separately; use the project identity markers
  # below for liveness so a stale port file cannot keep Unity marked active.
  # Prefer the exact PID recorded when this CLI launched Unity. This works
  # during startup before the socket server has written its port file.
  local pid_file="Temp/unity_cli_process.pid"
  if [ -f "$pid_file" ]; then
    local pid=""
    pid=$(cat "$pid_file" 2>/dev/null | tr -d '\r')
    pid="${pid#"${pid%%[![:space:]]*}"}"
    pid="${pid%"${pid##*[![:space:]]}"}"
    if is_process_alive "$pid"; then
      return 0
    fi
    rm -f "$pid_file" 2>/dev/null
  fi

  local lockfile=""
  if [ -f "Temp/UnityLockfile" ]; then
    lockfile="Temp/UnityLockfile"
  elif [ -f "Temp/UnityLockFile" ]; then
    lockfile="Temp/UnityLockFile"
  fi

  if is_windows_platform; then
    local windows_pid=""
    windows_pid=$(find_windows_unity_pid || true)
    if [[ "$windows_pid" =~ ^[0-9]+$ ]] && is_process_alive "$windows_pid"; then
      return 0
    fi

    if [ -n "$lockfile" ]; then
      remove_stale_unity_lockfiles
    fi
    return 1
  fi

  if [ -z "$lockfile" ]; then
    return 1
  fi

  # On Unix-like systems (Linux/macOS)
  # 1. Try flock command-line utility first (handles 0-byte lockfiles on Linux)
  if command -v flock >/dev/null 2>&1; then
    if ! flock -n "$lockfile" -c true >/dev/null 2>&1; then
      return 0 # Locked -> Unity is running
    fi
  fi

  # 2. Check the actual owner. This is required on macOS where the lockfile
  # is a zero-byte file and fuser may succeed without finding a process.
  local pid=""
  pid=$(get_unity_lock_owner_pid "$lockfile" 2>/dev/null || true)
  if [[ "$pid" =~ ^[0-9]+$ ]]; then
    return 0
  fi

  # Stale lockfile
  remove_stale_unity_lockfiles
  return 1
}

# Detect if Unity is running for this specific project
IS_RUNNING=false
AUTO_STARTED=false
if is_unity_still_running; then
  IS_RUNNING=true
fi

# Function to find Unity path
find_unity_path() {
  local configured_path="${UNITY_PATH:-${UNITY_EDITOR:-}}"
  if [ -n "$configured_path" ]; then
    local shell_configured_path=""
    shell_configured_path=$(to_shell_path "$configured_path" | tr -d '\r')
    if [ -f "$shell_configured_path" ]; then
      printf '%s\n' "$shell_configured_path"
      return 0
    fi
  fi

  # Prioritize unity-editor wrapper from PATH if explicitly requested via environment variable
  if [ "${USE_UNITY_EDITOR_WRAPPER:-false}" = "true" ]; then
    local container_unity=""
    container_unity=$(command -v unity-editor 2>/dev/null | tr -d '\r')
    if [ -n "$container_unity" ]; then
      echo "$container_unity"
      return 0
    fi
  fi

  local version=""
  if [ -f "ProjectSettings/ProjectVersion.txt" ]; then
    version=$(grep "m_EditorVersion:" ProjectSettings/ProjectVersion.txt | awk '{print $2}' | tr -d '\r')
  fi

  local paths=()
  if [ -n "$version" ]; then
    if is_windows_platform; then
      [ -n "${ProgramFiles:-}" ] && paths+=("$ProgramFiles/Unity/Hub/Editor/$version/Editor/Unity.exe")
      [ -n "${ProgramW6432:-}" ] && paths+=("$ProgramW6432/Unity/Hub/Editor/$version/Editor/Unity.exe")
      [ -n "${LOCALAPPDATA:-}" ] && paths+=("$LOCALAPPDATA/Unity/Hub/Editor/$version/Editor/Unity.exe")
      paths+=(
        "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
        "C:/Program Files (x86)/Unity/Hub/Editor/$version/Editor/Unity.exe"
        "C:/Unity/Hub/Editor/$version/Editor/Unity.exe"
      )
    elif [ "$PLATFORM" = "macos" ]; then
      paths=(
        "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
        "$HOME/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
      )
    else
      paths=(
        "$HOME/Unity/Hub/Editor/$version/Editor/Unity"
        "/opt/unity/Editor/Unity"
        "/opt/Unity/Editor/Unity"
      )
    fi
  fi

  for p in "${paths[@]}"; do
    local shell_path=""
    shell_path=$(to_shell_path "$p" | tr -d '\r')
    if [ -f "$shell_path" ]; then
      printf '%s\n' "$shell_path"
      return 0
    fi
  done

  local command_unity=""
  local cmd
  for cmd in unity-editor Unity Unity.exe unity.exe unity; do
    command_unity=$(command -v "$cmd" 2>/dev/null | tr -d '\r')
    if [ -n "$command_unity" ]; then
      printf '%s\n' "$command_unity"
      return 0
    fi
  done

  if is_windows_platform && command -v where.exe >/dev/null 2>&1; then
    command_unity=$(where.exe unity 2>/dev/null | head -n 1 | tr -d '\r')
    if [ -n "$command_unity" ]; then
      shell_path=$(to_shell_path "$command_unity" | tr -d '\r')
      if [ -f "$shell_path" ]; then
        printf '%s\n' "$shell_path"
        return 0
      fi
    fi
  fi

  return 1
}

# Function to start background Unity instance or wait for it to be ready
start_background_unity() {
  local mode="${1:-batchmode}"
  local unity_pid=""

  local needs_launch=true
  if is_unity_still_running; then
    needs_launch=false
    # Check if already ready
    if [ -f "Temp/unity_cli_port.txt" ] && is_unity_socket_ready 2; then
      local resp
      resp=$(send_socket_cmd "POLL_REFRESH" 2 2>/dev/null)
      if [ "$resp" = "READY" ] || [ "$resp" = "COMPILATION_ERROR" ]; then
        echo "Unity is already running."
        IS_RUNNING=true
        return 0
      fi
    fi
    echo "Unity is already running/starting. Skipping launch..."
    echo -n "Waiting for Unity background instance to be ready..."
  fi

  if [ "$needs_launch" = true ]; then
    UNITY_EXE=$(find_unity_path)
    if [ -z "$UNITY_EXE" ]; then
      local project_version=""
      if [ -f "ProjectSettings/ProjectVersion.txt" ]; then
        project_version=$(grep "m_EditorVersion:" ProjectSettings/ProjectVersion.txt | awk '{print $2}' | tr -d '\r')
      fi
      echo "Error: Unity executable not found for Unity version ${project_version:-unknown}."
      exit 1
    fi

    echo -n "Starting Unity background instance..."
    mkdir -p Temp
    rm -f unity_background_log.txt unity_stdout_stderr.txt
    
    # Run Unity in background (batchmode or interactive)
    local abs_proj_path
    abs_proj_path="$PROJECT_NATIVE_PATH"
    local auth_args=()
    local user="${UNITY_EMAIL:-${UNITY_USERNAME:-}}"
    if [ -n "$user" ] && [ -n "${UNITY_PASSWORD:-}" ] && [ -n "${UNITY_LICENSE:-}" ]; then
      local dev_data
      # Use POSIX sed; macOS's default grep does not support GNU -P.
      dev_data=$(printf '%s' "$UNITY_LICENSE" | tr -d '\r\n' | sed -n 's/.*<DeveloperData Value="\([^"]*\)".*/\1/p')
      if [ -n "$dev_data" ]; then
        local serial
        serial=$(printf '%s' "$dev_data" | decode_base64 | dd bs=1 skip=4 2>/dev/null)
        if [ -n "$serial" ]; then
          auth_args+=("-username" "$user" "-password" "$UNITY_PASSWORD" "-serial" "$serial")
        fi
      fi
    fi

    local launch_args=()
    if [ "$mode" = "batchmode" ]; then
      launch_args+=("-batchmode" "-nographics")
    fi
    launch_args+=("-projectPath" "$abs_proj_path" "-logFile" "unity_background_log.txt")
    if [ ${#auth_args[@]} -gt 0 ]; then
      launch_args+=("${auth_args[@]}")
    fi

    if is_windows_platform; then
      # Pass each argument through the environment so PowerShell does not
      # reinterpret spaces, quotes, dollar signs, or backslashes in values.
      local arg_count=${#launch_args[@]}
      local arg_index=0
      export UNITY_CLI_ARG_COUNT="$arg_count"
      for a in "${launch_args[@]}"; do
        export "UNITY_CLI_ARG_$arg_index=$a"
        arg_index=$((arg_index + 1))
      done
      local unity_exe_native="$UNITY_EXE"
      if [ -f "$UNITY_EXE" ]; then
        unity_exe_native=$(to_native_path "$UNITY_EXE" | tr -d '\r')
      fi
      local powershell_command=""
      powershell_command=$(find_powershell || true)
      if [ -n "$powershell_command" ]; then
        export UNITY_CLI_UNITY_EXE="$unity_exe_native"
        unity_pid=$("$powershell_command" -NoProfile -Command "
          \$arguments = @();
          for (\$i = 0; \$i -lt [int]\$env:UNITY_CLI_ARG_COUNT; \$i++) {
            \$arguments += [Environment]::GetEnvironmentVariable(('UNITY_CLI_ARG_' + \$i));
          }
          \$p = Start-Process -FilePath \$env:UNITY_CLI_UNITY_EXE -ArgumentList \$arguments -PassThru;
          \$p.Id
        " 2>/dev/null | tr -d '\r')
        unset UNITY_CLI_UNITY_EXE
      else
        # Git Bash can still launch Unity directly if PowerShell is unavailable;
        # the PID is retained as the best available fallback.
        "$UNITY_EXE" "${launch_args[@]}" >>unity_stdout_stderr.txt 2>&1 &
        unity_pid=$!
      fi
      arg_index=0
      while [ "$arg_index" -lt "$arg_count" ]; do
        unset "UNITY_CLI_ARG_$arg_index"
        arg_index=$((arg_index + 1))
      done
      unset UNITY_CLI_ARG_COUNT
    else
      nohup "$UNITY_EXE" "${launch_args[@]}" >>unity_stdout_stderr.txt 2>&1 &
      unity_pid=$!
    fi

    if [[ "$unity_pid" =~ ^[0-9]+$ ]]; then
      printf '%s\n' "$unity_pid" > Temp/unity_cli_process.pid
    fi
  fi

  local started=false
  # Wait up to 90 seconds (45 iterations * 2s sleep)
  for i in {1..45}; do
    if is_unity_still_running && [ -f "Temp/unity_cli_port.txt" ]; then
      if is_unity_socket_ready 2; then
        local response
        response=$(send_socket_cmd "POLL_REFRESH" 2 2>/dev/null)
        if [ "$response" = "READY" ] || [ "$response" = "COMPILATION_ERROR" ]; then
          echo ""
          if [ "$needs_launch" = true ]; then
            echo "Started successfully!"
          else
            echo "Unity is ready!"
          fi
          started=true
          break
        fi
      fi
    fi

    # Check for compilation errors in the log file
    if [ -f "unity_background_log.txt" ]; then
      if grep -q -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): error [a-zA-Z0-9]+:' "unity_background_log.txt"; then
        echo ""
        echo "Compilation errors detected during startup."
        # Sleep a moment to let all errors be written
        sleep 2
        # Extract all errors/warnings and save to Temp/unity_compilation_errors.txt
        mkdir -p Temp
        grep -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): (error|warning) [a-zA-Z0-9]+:' "unity_background_log.txt" | awk '!seen[$0]++' > Temp/unity_compilation_errors.txt
        parse_and_print_compilation_results "Temp/unity_compilation_errors.txt"
        if [ -n "$unity_pid" ]; then
          echo "Killing Unity process (PID $unity_pid)..."
          kill_process "$unity_pid"
        fi
        exit 1
      fi
    fi

    # Check if the process exited unexpectedly
    local process_exited=false
    if [ "$needs_launch" = true ] && [ -n "$unity_pid" ]; then
      if ! is_process_alive "$unity_pid"; then
        process_exited=true
      fi
    elif [ "$needs_launch" = false ]; then
      if ! is_unity_still_running; then
        process_exited=true
      fi
    fi

    if [ "$process_exited" = true ]; then
      echo ""
      echo "Unity process exited unexpectedly."
      if [ -f "unity_background_log.txt" ]; then
        if grep -q -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): error [a-zA-Z0-9]+:' "unity_background_log.txt"; then
          mkdir -p Temp
          grep -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): (error|warning) [a-zA-Z0-9]+:' "unity_background_log.txt" | awk '!seen[$0]++' > Temp/unity_compilation_errors.txt
          parse_and_print_compilation_results "Temp/unity_compilation_errors.txt"
          exit 1
        else
          echo "Last 20 lines of Unity log:"
          tail -n 20 "unity_background_log.txt"
          exit 1
        fi
      else
        echo "No Unity log file found."
        echo "Listing Temp directory:"
        ls -la Temp/ 2>/dev/null || echo "No Temp/ directory"
        if [ -f "unity_stdout_stderr.txt" ]; then
          echo "Contents of unity_stdout_stderr.txt:"
          cat "unity_stdout_stderr.txt"
        else
          echo "unity_stdout_stderr.txt does not exist"
        fi
        exit 1
      fi
    fi

    echo -n "."
    sleep 2
  done

  if [ "$started" = false ]; then
    echo ""
    echo "Failed to start background Unity instance or wait for it to be ready."
    exit 1
  fi
  IS_RUNNING=true
  if [ "$needs_launch" = true ]; then
    AUTO_STARTED=true
  fi
}

# Function to print failed tests in dotnet test format
print_failed_tests() {
  local failures_file="Temp/unity_test_failures.txt"
  local results_file="Temp/unity_test_results.json"

  if [ -f "$failures_file" ]; then
    cat "$failures_file"
    rm -f "$failures_file"
    rm -f "$results_file" 2>/dev/null
  fi
}

# PowerShell is deliberately the last JSON parser option and is only
# available on Windows after the portable parsers have been exhausted.
parse_json_with_powershell() {
  local result_type="$1"
  local results_file="$2"
  if ! is_windows_platform; then
    return 2
  fi

  local powershell_command=""
  powershell_command=$(find_powershell || true)
  if [ -z "$powershell_command" ]; then
    return 2
  fi

  local native_results_file="$results_file"
  if is_windows_platform; then
    native_results_file=$(to_native_path "$PROJECT_PATH/$results_file" | tr -d '\r')
  fi
  export UNITY_CLI_RESULT_FILE="$native_results_file"
  local status=0
  case "$result_type" in
    test)
      "$powershell_command" -NoProfile -Command "
        \$content = Get-Content \$env:UNITY_CLI_RESULT_FILE -Raw -Encoding UTF8;
        \$json = ConvertFrom-Json \$content;
        \$skip = if (\$json.skipCount -and \$json.skipCount -gt 0) { \", \$(\$json.skipCount) skipped\" } else { \"\" };
        Write-Output \"\"
        Write-Output \"Done!\"
        if (\$json.success) {
          Write-Output \"Unity Response: SUCCESS \$(\$json.passCount) passed\$skip\";
          exit 0;
        }
        if (\$json.message -ne \$null -and \$json.message -ne '') {
          Write-Output \"Unity Response: FAILURE \$(\$json.message)\";
        } else {
          Write-Output \"Unity Response: FAILURE \$(\$json.failCount) failed, \$(\$json.passCount) passed\$skip\";
        }
        exit 1;
      " || status=$?
      ;;
    execute)
      "$powershell_command" -NoProfile -Command "
        \$content = Get-Content \$env:UNITY_CLI_RESULT_FILE -Raw -Encoding UTF8;
        \$json = ConvertFrom-Json \$content;
        Write-Output \"\"
        Write-Output \"Done!\"
        if (\$json.success) {
          if (\$json.payload -ne \$null -and \$json.payload -ne '') {
            Write-Output \$json.payload;
          } else {
            Write-Output \"Unity Response: SUCCESS\";
          }
          exit 0;
        }
        Write-Output \"Unity Response: FAILURE\";
        Write-Output \$json.message;
        exit 1;
      " || status=$?
      ;;
    eval)
      "$powershell_command" -NoProfile -Command "
        \$content = Get-Content \$env:UNITY_CLI_RESULT_FILE -Raw -Encoding UTF8;
        \$json = ConvertFrom-Json \$content;
        if (\$json.success) {
          if (\$json.payload -ne \$null -and \$json.payload -ne '') {
            Write-Output \$json.payload;
          }
          exit 0;
        }
        [Console]::Error.WriteLine(\$json.message);
        exit 1;
      " || status=$?
      ;;
    *)
      status=2
      ;;
  esac
  unset UNITY_CLI_RESULT_FILE
  return $status
}

# Function to parse test results from Temp/unity_test_results.json if process exited/recycled
parse_and_handle_test_results_file() {
  local results_file="Temp/unity_test_results.json"
  if [ ! -f "$results_file" ]; then
    return 2
  fi

  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys, json; sys.exit(0)' 2>/dev/null; then
    python3 -c '
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        data = json.load(f)
    pass_cnt = data.get("passCount", 0)
    fail_cnt = data.get("failCount", 0)
    skip_cnt = data.get("skipCount", 0)
    skip_str = f", {skip_cnt} skipped" if skip_cnt > 0 else ""
    print("\nDone!")
    if data.get("success"):
        print(f"Unity Response: SUCCESS {pass_cnt} passed{skip_str}")
        sys.exit(0)
    else:
        msg = data.get("message")
        if msg:
            print(f"Unity Response: FAILURE {msg}")
        else:
            print(f"Unity Response: FAILURE {fail_cnt} failed, {pass_cnt} passed{skip_str}")
        sys.exit(1)
except Exception:
    sys.exit(1)
' "$results_file"
    local status=$?
    if [ $status -ne 0 ]; then
      print_failed_tests
    fi
    return $status
  elif command -v python >/dev/null 2>&1 && python -c 'import sys; sys.exit(0 if sys.version_info[0] >= 3 else 1)' 2>/dev/null; then
    python -c '
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        data = json.load(f)
    pass_cnt = data.get("passCount", 0)
    fail_cnt = data.get("failCount", 0)
    skip_cnt = data.get("skipCount", 0)
    skip_str = f", {skip_cnt} skipped" if skip_cnt > 0 else ""
    print("\nDone!")
    if data.get("success"):
        print(f"Unity Response: SUCCESS {pass_cnt} passed{skip_str}")
        sys.exit(0)
    else:
        msg = data.get("message")
        if msg:
            print(f"Unity Response: FAILURE {msg}")
        else:
            print(f"Unity Response: FAILURE {fail_cnt} failed, {pass_cnt} passed{skip_str}")
        sys.exit(1)
except Exception:
    sys.exit(1)
' "$results_file"
    local status=$?
    if [ $status -ne 0 ]; then
      print_failed_tests
    fi
    return $status
  elif command -v perl >/dev/null 2>&1 && perl -MJSON::PP -e 'exit 0' 2>/dev/null; then
    perl -MJSON::PP -e '
      local $/;
      open(my $f, "<:utf8", $ARGV[0]) or exit 2;
      my $data = decode_json(<$f>);
      my $pass_cnt = $data->{passCount} || 0;
      my $fail_cnt = $data->{failCount} || 0;
      my $skip_cnt = $data->{skipCount} || 0;
      my $skip_str = $skip_cnt > 0 ? ", $skip_cnt skipped" : "";
      print "\nDone!\n";
      if ($data->{success}) {
        print "Unity Response: SUCCESS $pass_cnt passed$skip_str\n";
        exit 0;
      } else {
        my $msg = $data->{message};
        if (defined $msg && $msg ne "") {
          print "Unity Response: FAILURE $msg\n";
        } else {
          print "Unity Response: FAILURE $fail_cnt failed, $pass_cnt passed$skip_str\n";
        }
        exit 1;
      }
    ' "$results_file"
    local status=$?
    if [ $status -ne 0 ]; then
      print_failed_tests
    fi
    return $status
  elif command -v node >/dev/null 2>&1; then
    node -e '
      const fs = require("fs");
      const data = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
      const skip = data.skipCount > 0 ? `, ${data.skipCount} skipped` : "";
      console.log("\nDone!");
      if (data.success) {
        console.log(`Unity Response: SUCCESS ${data.passCount || 0} passed${skip}`);
        process.exit(0);
      }
      if (data.message) console.log(`Unity Response: FAILURE ${data.message}`);
      else console.log(`Unity Response: FAILURE ${data.failCount || 0} failed, ${data.passCount || 0} passed${skip}`);
      process.exit(1);
    ' "$results_file"
    local status=$?
    if [ $status -ne 0 ]; then
      print_failed_tests
    fi
    return $status
  elif command -v jq >/dev/null 2>&1 && jq empty "$results_file" >/dev/null 2>&1; then
    echo ""
    echo "Done!"
    local success
    success=$(jq -r '.success' "$results_file")
    if [ "$success" = "true" ]; then
      local skip_count
      skip_count=$(jq -r '.skipCount // 0' "$results_file")
      local skip_str=""
      if [ "$skip_count" -gt 0 ]; then
        skip_str=", $skip_count skipped"
      fi
      echo "Unity Response: SUCCESS $(jq -r '.passCount // 0' "$results_file") passed$skip_str"
      return 0
    fi
    local message
    message=$(jq -r '.message // empty' "$results_file")
    if [ -n "$message" ]; then
      echo "Unity Response: FAILURE $message"
    else
      echo "Unity Response: FAILURE $(jq -r '.failCount // 0' "$results_file") failed, $(jq -r '.passCount // 0' "$results_file") passed"
    fi
    print_failed_tests
    return 1
  fi

  if is_windows_platform; then
    parse_json_with_powershell "test" "$results_file"
    local status=$?
    if [ "$status" -ne 2 ]; then
      if [ "$status" -ne 0 ]; then
        print_failed_tests
      fi
      return "$status"
    fi
  fi

  local success
  success=$(grep '"success":' "$results_file" 2>/dev/null | sed -E 's/.*"success":[[:space:]]*(true|false).*/\1/' | head -n 1)
  local pass_count
  pass_count=$(grep '"passCount":' "$results_file" 2>/dev/null | sed -E 's/.*"passCount":[[:space:]]*([0-9]+).*/\1/' | head -n 1)
  local fail_count
  fail_count=$(grep '"failCount":' "$results_file" 2>/dev/null | sed -E 's/.*"failCount":[[:space:]]*([0-9]+).*/\1/' | head -n 1)
  local skip_count
  skip_count=$(grep '"skipCount":' "$results_file" 2>/dev/null | sed -E 's/.*"skipCount":[[:space:]]*([0-9]+).*/\1/' | head -n 1)
  local msg
  msg=$(grep '"message":' "$results_file" 2>/dev/null | sed -E 's/.*"message":[[:space:]]*"(([^"\\]|\\.)*)".*/\1/' | sed 's/\\"/"/g' | head -n 1)

  local skip_str=""
  if [ -n "$skip_count" ] && [ "$skip_count" -gt 0 ]; then
    skip_str=", $skip_count skipped"
  fi

  if [ "$success" = "true" ]; then
    echo ""
    echo "Done!"
    echo "Unity Response: SUCCESS ${pass_count:-0} passed${skip_str}"
    return 0
  else
    echo ""
    echo "Done!"
    if [ -n "$msg" ]; then
      echo "Unity Response: FAILURE $msg"
    else
      echo "Unity Response: FAILURE ${fail_count:-0} failed, ${pass_count:-0} passed${skip_str}"
    fi
    print_failed_tests
    return 1
  fi
}

# Function to parse execute results from Temp/unity_execute_result.json if process exited/recycled
parse_and_handle_execute_results_file() {
  local results_file="Temp/unity_execute_result.json"
  if [ ! -f "$results_file" ]; then
    return 2
  fi

  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys, json; sys.exit(0)' 2>/dev/null; then
    python3 -c '
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        data = json.load(f)
    print("\nDone!")
    if data.get("success"):
        payload = data.get("payload")
        if payload is not None and payload != "":
            print(payload)
        else:
            print("Unity Response: SUCCESS")
        sys.exit(0)
    else:
        print("Unity Response: FAILURE")
        print(data.get("message", ""))
        sys.exit(1)
except Exception:
    sys.exit(1)
' "$results_file"
    return $?
  elif command -v perl >/dev/null 2>&1 && perl -MJSON::PP -e 'exit 0' 2>/dev/null; then
    perl -MJSON::PP -e '
      local $/;
      open(my $f, "<:utf8", $ARGV[0]) or exit 2;
      my $data = decode_json(<$f>);
      print "\nDone!\n";
      if ($data->{success}) {
        if (defined $data->{payload} && $data->{payload} ne "") {
          print $data->{payload} . "\n";
        } else {
          print "Unity Response: SUCCESS\n";
        }
        exit 0;
      } else {
        print "Unity Response: FAILURE\n";
        print ($data->{message} || "") . "\n";
        exit 1;
      }
    ' "$results_file"
    return $?
  elif command -v node >/dev/null 2>&1; then
    node -e '
      const fs = require("fs");
      const data = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
      console.log("\nDone!");
      if (data.success) {
        if (data.payload !== null && data.payload !== undefined && data.payload !== "") console.log(data.payload);
        else console.log("Unity Response: SUCCESS");
        process.exit(0);
      } else {
        console.log("Unity Response: FAILURE");
        console.log(data.message || "");
        process.exit(1);
      }
    ' "$results_file"
    return $?
  elif command -v jq >/dev/null 2>&1; then
    echo ""
    echo "Done!"
    if jq -e '.success' "$results_file" >/dev/null 2>&1; then
      local pl
      pl=$(jq -r '.payload // empty' "$results_file")
      if [ -n "$pl" ]; then
        echo "$pl"
      else
        echo "Unity Response: SUCCESS"
      fi
      return 0
    else
      echo "Unity Response: FAILURE"
      jq -r '.message // ""' "$results_file"
      return 1
    fi
  fi

  if is_windows_platform; then
    parse_json_with_powershell "execute" "$results_file"
    local status=$?
    if [ "$status" -ne 2 ]; then
      return "$status"
    fi
  fi

  local success
  success=$(grep '"success":' "$results_file" 2>/dev/null | sed -E 's/.*"success":[[:space:]]*(true|false).*/\1/' | head -n 1)
  local payload
  payload=$(grep '"payload":' "$results_file" 2>/dev/null | sed -E 's/.*"payload":[[:space:]]*"(([^"\\]|\\.)*)".*/\1/' | sed 's/\\"/"/g' | head -n 1)
  local msg
  msg=$(grep '"message":' "$results_file" 2>/dev/null | sed -E 's/.*"message":[[:space:]]*"(([^"\\]|\\.)*)".*/\1/' | sed 's/\\"/"/g' | head -n 1)

  if [ "$success" = "true" ]; then
    echo ""
    echo "Done!"
    if [ -n "$payload" ]; then
      echo "$payload"
    else
      echo "Unity Response: SUCCESS"
    fi
    return 0
  else
    echo ""
    echo "Done!"
    echo "Unity Response: FAILURE"
    echo "$msg"
    return 1
  fi
}

# Function to parse eval results from Temp/unity_eval_result.json
parse_and_handle_eval_results_file() {
  local results_file="Temp/unity_eval_result.json"
  if [ ! -f "$results_file" ]; then
    return 2
  fi

  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys, json; sys.exit(0)' 2>/dev/null; then
    python3 -c '
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        data = json.load(f)
    if data.get("success"):
        payload = data.get("payload")
        if payload is not None and payload != "":
            print(payload)
        sys.exit(0)
    else:
        msg = data.get("message", "Evaluation failed.")
        print(msg, file=sys.stderr)
        sys.exit(1)
except Exception as e:
    print(e, file=sys.stderr)
    sys.exit(1)
' "$results_file"
    return $?
  elif command -v python >/dev/null 2>&1 && python -c 'import sys, json; sys.exit(0)' 2>/dev/null; then
    python -c '
import json, sys
try:
    with open(sys.argv[1], "r", encoding="utf-8") as f:
        data = json.load(f)
    if data.get("success"):
        payload = data.get("payload")
        if payload is not None and payload != "":
            print(payload)
        sys.exit(0)
    else:
        msg = data.get("message", "Evaluation failed.")
        print(msg, file=sys.stderr)
        sys.exit(1)
except Exception as e:
    print(e, file=sys.stderr)
    sys.exit(1)
' "$results_file"
    return $?
  elif command -v perl >/dev/null 2>&1 && perl -MJSON::PP -e 'exit 0' 2>/dev/null; then
    perl -MJSON::PP -e '
      local $/;
      open(my $f, "<:utf8", $ARGV[0]) or exit 2;
      my $data = decode_json(<$f>);
      if ($data->{success}) {
        print $data->{payload} . "\n" if defined $data->{payload} && $data->{payload} ne "";
        exit 0;
      } else {
        warn ($data->{message} || "Evaluation failed.") . "\n";
        exit 1;
      }
    ' "$results_file"
    return $?
  elif command -v node >/dev/null 2>&1; then
    node -e '
      const fs = require("fs");
      const data = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
      if (data.success) {
        if (data.payload !== null && data.payload !== undefined && data.payload !== "") console.log(data.payload);
        process.exit(0);
      } else {
        console.error(data.message || "Evaluation failed.");
        process.exit(1);
      }
    ' "$results_file"
    return $?
  elif command -v jq >/dev/null 2>&1; then
    if jq -e '.success' "$results_file" >/dev/null 2>&1; then
      local pl
      pl=$(jq -r '.payload // empty' "$results_file")
      if [ -n "$pl" ]; then
        echo "$pl"
      fi
      return 0
    else
      jq -r '.message // "Evaluation failed."' "$results_file" >&2
      return 1
    fi
  fi

  if is_windows_platform; then
    parse_json_with_powershell "eval" "$results_file"
    local status=$?
    if [ "$status" -ne 2 ]; then
      return "$status"
    fi
  fi

  local success
  success=$(grep '"success":' "$results_file" 2>/dev/null | sed -E 's/.*"success":[[:space:]]*(true|false).*/\1/' | head -n 1)
  local payload
  payload=$(grep '"payload":' "$results_file" 2>/dev/null | sed -E 's/.*"payload":[[:space:]]*"(([^"\\]|\\.)*)".*/\1/' | sed 's/\\"/"/g' | head -n 1)
  local msg
  msg=$(grep '"message":' "$results_file" 2>/dev/null | sed -E 's/.*"message":[[:space:]]*"(([^"\\]|\\.)*)".*/\1/' | sed 's/\\"/"/g' | head -n 1)

  if [ "$success" = "true" ]; then
    if [ -n "$payload" ] && [ "$payload" != "null" ]; then
      echo "$payload"
    elif [ "$payload" = "null" ]; then
      echo "null"
    fi
    return 0
  else
    if [ -n "$msg" ]; then
      echo "$msg" >&2
    else
      echo "Evaluation failed." >&2
    fi
    return 1
  fi
}

# Function to run tests via socket (Online)
run_online_tests() {
  local mode="$1"
  echo "Sending command to run $mode tests..."

  local response=""
  local cmd="RUN_TESTS $mode"
  if [ -n "$FILTER" ]; then
    cmd="$cmd --filter \"$FILTER\""
  fi
  if [ -n "$CATEGORY" ]; then
    cmd="$cmd --category \"$CATEGORY\""
  fi
  response=$(send_socket_cmd "$cmd" 10)
  if [ $? -ne 0 ] || [ -z "$response" ] || [[ "$response" == ERROR* ]] || [[ "$response" == FAILURE* ]]; then
    echo "Unity Response: $response"
    return 1
  fi
  
  echo -n "Waiting for tests to complete..."
  while true; do
    sleep 1

    # Re-read port/query status. The connection will fail during domain reloads, which is expected.
    response=$(send_socket_cmd "POLL_TESTS" 5)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      if [ -f "Temp/unity_test_results.json" ] && [ ! -f "Temp/unity_test_running.txt" ]; then
        parse_and_handle_test_results_file
        return $?
      fi

      if ! is_unity_still_running; then
        for wait_i in {1..10}; do
          if [ -f "Temp/unity_test_results.json" ]; then
            parse_and_handle_test_results_file
            return $?
          fi
          sleep 0.3
        done

        echo ""
        echo "Error: Unity background process exited during test execution."
        return 1
      fi
      echo -n "."
      continue
    fi

    if [ "$response" = "RUNNING" ]; then
      echo -n "."
    elif [[ "$response" == SUCCESS* ]]; then
      echo ""
      echo "Done!"
      echo "Unity Response: $response"
      return 0
    elif [[ "$response" == FAILURE* ]]; then
      echo ""
      echo "Done!"
      echo "Unity Response: $response"
      print_failed_tests
      return 1
    else
      # If IDLE or ERROR
      echo ""
      echo "Done!"
      echo "Unity Response: $response"
      return 2
    fi
  done
}

# Function to run a method via socket (Online)
run_online_method() {
  echo "Sending command to run method $EXECUTE_METHOD..."

  local cmd="EXECUTE_METHOD $EXECUTE_METHOD"
  if [ ${#EXECUTE_METHOD_PARAMS[@]} -gt 0 ]; then
    for param in "${EXECUTE_METHOD_PARAMS[@]}"; do
      local escaped="${param//\\/\\\\}"
      escaped="${escaped//\"/\\\"}"
      cmd="$cmd \"$escaped\""
    done
  fi

  local response=""
  response=$(send_socket_cmd "$cmd" 10)
  if [ $? -ne 0 ] || [[ "$response" == ERROR* ]]; then
    echo "Error starting method execution: $response"
    return 1
  fi

  echo -n "Waiting for method execution to complete..."
  while true; do
    sleep 1

    response=$(send_socket_cmd "POLL_EXECUTE" 5)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      if [ -f "Temp/unity_execute_result.json" ] && [ ! -f "Temp/unity_execute_running.txt" ]; then
        parse_and_handle_execute_results_file
        return $?
      fi

      if ! is_unity_still_running; then
        for wait_i in {1..10}; do
          if [ -f "Temp/unity_execute_result.json" ]; then
            parse_and_handle_execute_results_file
            return $?
          fi
          sleep 0.3
        done

        echo ""
        echo "Error: Unity background process exited during method execution."
        return 1
      fi
      echo -n "."
      continue
    fi

    if [ "$response" = "RUNNING" ]; then
      echo -n "."
    elif [[ "$response" == SUCCESS* ]]; then
      echo ""
      echo "Done!"
      local payload="${response#SUCCESS}"
      # Trim leading/trailing whitespace
      payload="${payload#"${payload%%[![:space:]]*}"}"
      payload="${payload%"${payload##*[![:space:]]}"}"
      if [ -n "$payload" ]; then
        echo "$payload"
      else
        echo "Unity Response: SUCCESS"
      fi
      return 0
    elif [[ "$response" == FAILURE* ]]; then
      echo ""
      echo "Done!"
      echo "Unity Response: FAILURE"
      echo "${response#FAILURE }"
      return 1
    else
      echo ""
      echo "Done!"
      echo "Unity Response: $response"
      return 2
    fi
  done
}

# Function to evaluate C# snippet via socket (Online)
run_online_eval() {
  local cmd="EVAL $EVAL_CODE"
  local response=""

  response=$(send_socket_cmd "$cmd" 30)
  local send_status=$?

  if [ -f "Temp/unity_eval_result.json" ] && [ ! -f "Temp/unity_eval_running.txt" ]; then
    parse_and_handle_eval_results_file
    return $?
  fi

  if [ $send_status -ne 0 ] || [ -z "$response" ]; then
    if ! is_unity_still_running; then
      echo "Error: Unity background process exited during eval execution." >&2
      return 1
    fi

    # Poll if needed
    local elapsed=0
    while [ $elapsed -lt 30 ]; do
      sleep 0.2
      elapsed=$((elapsed + 1))
      response=$(send_socket_cmd "POLL_EVAL" 5 2>/dev/null)
      if [ -f "Temp/unity_eval_result.json" ] && [ ! -f "Temp/unity_eval_running.txt" ]; then
        parse_and_handle_eval_results_file
        return $?
      fi
      if [ "$response" = "RUNNING" ]; then
        continue
      elif [[ "$response" == SUCCESS* ]] || [[ "$response" == FAILURE* ]] || [[ "$response" == ERROR* ]]; then
        break
      fi
    done
  fi

  if [ -f "Temp/unity_eval_result.json" ]; then
    parse_and_handle_eval_results_file
    return $?
  fi

  if [[ "$response" == SUCCESS* ]]; then
    local payload="${response#SUCCESS}"
    payload="${payload#"${payload%%[![:space:]]*}"}"
    payload="${payload%"${payload##*[![:space:]]}"}"
    if [ -n "$payload" ]; then
      echo "$payload"
    fi
    return 0
  else
    local err="${response#FAILURE}"
    err="${err#ERROR:}"
    err="${err#"${err%%[![:space:]]*}"}"
    err="${err%"${err##*[![:space:]]}"}"
    echo "$err" >&2
    return 1
  fi
}

# Function to parse compilation errors and warnings from a log file
# and print them in dotnet build format.
parse_and_print_compilation_results() {
  local log_file="$1"
  if [ ! -f "$log_file" ]; then
    return 1
  fi

  # Extract lines matching compiler error/warning pattern from the file,
  # and deduplicate preserving order
  local lines
  lines=$(grep -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): (error|warning) [a-zA-Z0-9]+:' "$log_file" | awk '!seen[$0]++')

  if [ -z "$lines" ]; then
    return 1
  fi

  local error_count=0
  local warning_count=0

  # ANSI color codes
  local red=$'\e[31m'
  local yellow=$'\e[33m'
  local reset=$'\e[0m'

  # Read line by line to count and format
  while IFS= read -r line; do
    if [ -z "$line" ]; then
      continue
    fi
    if [[ "$line" =~ \):\ error\  ]]; then
      ((error_count++))
      echo "${line/error /${red}error${reset} }"
    elif [[ "$line" =~ \):\ warning\  ]]; then
      ((warning_count++))
      echo "${line/warning /${yellow}warning${reset} }"
    else
      echo "$line"
    fi
  done <<< "$lines"

  echo ""
  if [ $error_count -gt 0 ]; then
    echo "${red}Build FAILED.${reset}"
    echo "    $warning_count Warning(s)"
    echo "    $error_count Error(s)"
    return 0 # compilation failed
  else
    echo "${yellow}Build succeeded with warnings.${reset}"
    echo "    $warning_count Warning(s)"
    echo "    $error_count Error(s)"
    return 2 # compilation succeeded but with warnings
  fi
}

# --- Main Execution ---

# Clean up transient runtime markers and previous test/execute/eval result files
rm -f Temp/unity_test_running.txt Temp/unity_test_results.json Temp/unity_test_failures.txt 2>/dev/null
rm -f Temp/unity_execute_result.json Temp/unity_execute_running.txt Temp/unity_eval_result.json Temp/unity_eval_running.txt 2>/dev/null

if [ "$SUBCOMMAND" = "start" ]; then
  start_background_unity "$BG_MODE"
  exit 0

elif [ "$SUBCOMMAND" = "stop" ]; then
  running=false
  if [ "$IS_RUNNING" = true ]; then
    running=true
  fi

  if [ "$running" = false ]; then
    echo "Unity background instance is not running."
    exit 0
  fi

  echo -n "Stopping Unity background instance..."

  stopped=false
  if is_unity_socket_ready 2; then
    response=$(send_socket_cmd "EXIT" 5 2>/dev/null)
    if [ "$response" = "EXITING" ]; then
      # Wait up to 15 seconds for Unity and its socket to disappear.
      for i in {1..15}; do
        if ! is_unity_still_running; then
          stopped=true
          break
        fi
        sleep 1
      done
    fi
  fi

  if [ "$stopped" = true ]; then
    rm -f Temp/unity_cli_process.pid 2>/dev/null
    echo ""
    echo "Stopped cleanly."
    exit 0
  fi

  # Fallback to process kill
  lockfile=""
  if [ -f "Temp/UnityLockfile" ]; then
    lockfile="Temp/UnityLockfile"
  elif [ -f "Temp/UnityLockFile" ]; then
    lockfile="Temp/UnityLockFile"
  fi

  pid=""
  if [ -f "Temp/unity_cli_process.pid" ]; then
    pid=$(cat "Temp/unity_cli_process.pid" 2>/dev/null | tr -d '\r')
    pid="${pid#"${pid%%[![:space:]]*}"}"
    pid="${pid%"${pid##*[![:space:]]}"}"
  fi

  if [ -z "$pid" ] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then
    if is_windows_platform; then
      pid=$(find_windows_unity_pid || true)
    elif [ -n "$lockfile" ]; then
      pid=$(get_unity_lock_owner_pid "$lockfile" 2>/dev/null || true)
    fi
  fi

  if [[ "$pid" =~ ^[0-9]+$ ]]; then
    if ! kill_process "$pid"; then
      echo ""
      echo "Error: Could not stop Unity process (PID $pid)." >&2
      exit 1
    fi
    rm -f Temp/unity_cli_process.pid 2>/dev/null
    # Wait up to 15 seconds for the project-specific Unity process to stop.
    for i in {1..15}; do
      if ! is_unity_still_running; then
        stopped=true
        break
      fi
      sleep 1
    done
  else
    echo ""
    echo "Error: Could not identify the Unity process for this project; refusing to terminate by image name." >&2
    exit 1
  fi

  if [ "$stopped" = true ]; then
    echo ""
    echo "Stopped."
    exit 0
  fi

  echo ""
  echo "Error: Unity background instance could not be stopped." >&2
  echo "The project lockfile or socket is still active; no process was killed." >&2
  exit 1
elif [ "$SUBCOMMAND" = "wait-ready" ]; then
  if [ "$IS_RUNNING" = false ]; then
    echo "Error: Unity is not running for this project."
    exit 1
  fi

  echo -n "Unity is running. Connecting..."
  while true; do
    if _=$(send_socket_cmd "PING" 2 2>/dev/null); then
      echo ""
      echo "Connected successfully!"
      exit 0
    fi
    echo -n "."
    sleep 1
  done
elif [ "$SUBCOMMAND" = "status" ]; then
  if [ "$IS_RUNNING" = false ]; then
    echo "Status: Not Running"
    exit 0
  fi

  response=""
  response=$(send_socket_cmd "PING" 2 2>/dev/null)
  socket_exit_code=$?
  if [ "$socket_exit_code" -eq 0 ] && [ "$response" = "PONG" ]; then
    echo "Status: Ready"
  elif [ "$socket_exit_code" -eq 42 ]; then
    print_network_permission_error
    echo "Status: Local Network Permission Required"
  else
    echo "Status: Running Unreachable"
  fi
  exit 0
fi

if [ "$SUBCOMMAND" = "eval" ]; then
  if [ "$IS_RUNNING" = false ]; then
    start_background_unity batchmode
  fi
  run_online_eval
  exit $?
fi

if [ "$IS_RUNNING" = false ]; then
  if [ "$SUBCOMMAND" = "refresh" ] || [ "$SUBCOMMAND" = "recompile" ] || [ "$SUBCOMMAND" = "test" ] || [ "$SUBCOMMAND" = "executemethod" ]; then
    start_background_unity batchmode
  fi
fi

if [ "$IS_RUNNING" = true ]; then
  if [ "$AUTO_STARTED" = false ]; then
    echo "Detected running Unity instance (via UnityLockfile)."
  fi
  
  # Step 1: Trigger AssetDatabase refresh or recompile
  if [ "$SUBCOMMAND" = "recompile" ]; then
    echo -n "Triggering force recompilation..."
    while true; do
      if _=$(send_socket_cmd "RECOMPILE" 2>/dev/null); then
        echo ""
        echo "Done!"
        break
      fi

      socket_exit_code=$?
      if [ "$socket_exit_code" -eq 42 ]; then
        echo ""
        print_network_permission_error
        exit 1
      fi

      if ! is_unity_still_running; then
        echo ""
        echo "Error: Unity background process exited before recompilation could be triggered."
        exit 1
      fi
      
      echo -n "."
      sleep 1
    done
  else
    echo -n "Triggering AssetDatabase refresh..."
    while true; do
      if _=$(send_socket_cmd "REFRESH" 2>/dev/null); then
        echo ""
        echo "Done!"
        break
      fi

      socket_exit_code=$?
      if [ "$socket_exit_code" -eq 42 ]; then
        echo ""
        print_network_permission_error
        exit 1
      fi
      
      # If connection failed, check if Unity is still running.
      # If it's not running, we should abort instead of looping forever.
      if ! is_unity_still_running; then
        echo ""
        echo "Error: Unity background process exited before asset refresh could be triggered."
        exit 1
      fi
      
      echo -n "."
      sleep 1
    done
  fi
fi

if [ "$IS_RUNNING" = true ]; then
  # Step 2: Poll refresh/recompile until READY
  if [ "$SUBCOMMAND" = "recompile" ]; then
    echo -n "Waiting for recompilation to finish..."
  else
    echo -n "Waiting for AssetDatabase refresh/compilation to finish..."
  fi
  refresh_timeout="${UNITY_CLI_REFRESH_TIMEOUT:-120}"
  elapsed=0
  while true; do
    # Sleep 1s
    sleep 1
    elapsed=$((elapsed + 1))
    
    # Check status. send_socket_cmd reads the port file for each connection attempt.
    response=""
    response=$(send_socket_cmd "POLL_REFRESH" 2)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      if ! is_unity_still_running; then
        echo ""
        if [ "$SUBCOMMAND" = "recompile" ]; then
          echo "Error: Unity background process exited during recompilation."
        else
          echo "Error: Unity background process exited during asset refresh/compilation."
        fi
        exit 1
      fi
      # Connection failure (compiling or domain reload in progress)
      if [ "$elapsed" -ge "$refresh_timeout" ]; then
        echo ""
        echo "Error: Timed out waiting for AssetDatabase refresh/compilation to finish ($refresh_timeout seconds)." >&2
        echo "Unity background instance is unresponsive. Check Unity Editor logs or restart the background instance." >&2
        exit 1
      fi
      echo -n "."
      continue
    fi
    
    if [ "$response" = "READY" ]; then
      echo ""
      echo "Unity is ready!"
      if [ -f "Temp/unity_compilation_errors.txt" ]; then
        parse_and_print_compilation_results "Temp/unity_compilation_errors.txt"
        parse_status=$?
        if [ $parse_status -eq 0 ]; then
          exit 1
        fi
      fi
      break
    elif [ "$response" = "COMPILATION_ERROR" ]; then
      echo ""
      for wait_diag in {1..25}; do
        if [ -s "Temp/unity_compilation_errors.txt" ]; then
          break
        fi
        sleep 0.1
      done
      sleep 0.2
      if [ -f "Temp/unity_compilation_errors.txt" ] && parse_and_print_compilation_results "Temp/unity_compilation_errors.txt"; then
        :
      else
        echo "Error: Unity compilation failed. Check the Unity Editor Console for details."
      fi
      exit 1
    else
      if [ "$elapsed" -ge "$refresh_timeout" ]; then
        echo ""
        echo "Error: Timed out waiting for AssetDatabase refresh/compilation to finish ($refresh_timeout seconds). Last status: $response" >&2
        echo "Unity may be stuck in an infinite asset import loop, modal dialog, or corrupted Library cache." >&2
        exit 1
      fi
      echo -n "."
    fi
  done

  # Step 3: Action Execution
  if [ "$SUBCOMMAND" = "refresh" ] || [ "$SUBCOMMAND" = "recompile" ]; then
    if [ "$SUBCOMMAND" = "recompile" ]; then
      echo "Recompilation completed."
    else
      echo "Refresh completed."
    fi
    exit 0
  elif [ "$SUBCOMMAND" = "executemethod" ]; then
    run_online_method
    exit_code=$?
    if [ $exit_code -ne 0 ]; then
      echo "Method execution failed."
      exit 1
    else
      echo "Method execution succeeded."
      exit 0
    fi
  else
    # SUBCOMMAND is test
    TESTS_FAILED=false
    if [ "$MODE_EDITMODE" = true ]; then
      run_online_tests "editmode"
      if [ $? -ne 0 ]; then
        TESTS_FAILED=true
      fi
    fi

    if [ "$MODE_PLAYMODE" = true ]; then
      run_online_tests "playmode"
      if [ $? -ne 0 ]; then
        TESTS_FAILED=true
      fi
    fi

    if [ "$TESTS_FAILED" = true ]; then
      echo "Some tests failed."
      exit 1
    else
      echo "All tests passed."
      exit 0
    fi
  fi
fi
