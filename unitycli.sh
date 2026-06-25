#!/usr/bin/env bash

# Exit immediately if a command exits with a non-zero status,
# but we handle Unity exit codes manually.
set -u

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
BG_ACTION=""
BG_MODE=""

# Helper for usage
show_usage() {
  echo "Usage: $0 <command> [options]"
  echo "Commands:"
  echo "  start <mode>            Start a background Unity instance (mode: batchmode | interactive)"
  echo "  stop                    Stop the background Unity instance"
  echo "  status                  Check status of the background Unity instance"
  echo "  refresh                 Trigger AssetDatabase refresh and print compiler diagnostics"
  echo "  test [options]          Run tests (defaults to running both EditMode and PlayMode)"
  echo "    --playmode            Run PlayMode tests"
  echo "    --editmode            Run EditMode tests"
  echo "    --filter <filter>     Filter tests by name (regex/substring)"
  echo "    --category <category> Filter tests by category"
  echo "  executemethod <method> [args...] Execute a custom static method (optionally with parameters)"
  echo "                          (e.g., Namespace.Class.Method 4 3 \"{\\\"Value\\\":4}\")"

  echo "  -h, --help              Show this help message"
  exit 1
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
          show_usage
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
    show_usage
    ;;

  *)
    echo "Unknown command: $SUBCOMMAND"
    show_usage
    ;;
esac

# Function to check if Unity is still running (locked)
is_unity_still_running() {
  local lockfile=""
  if [ -f "Temp/UnityLockfile" ]; then
    lockfile="Temp/UnityLockfile"
  elif [ -f "Temp/UnityLockFile" ]; then
    lockfile="Temp/UnityLockFile"
  fi

  if [ -z "$lockfile" ]; then
    return 1
  fi

  # Check if we are on Windows
  if [[ "${OSTYPE:-}" == "msys" || "${OSTYPE:-}" == "cygwin" || "${OSTYPE:-}" == "mingw"* || "${OS:-}" == "Windows_NT" ]]; then
    # On Windows, if the file is locked by a running Unity instance, cat will fail.
    if ! cat "$lockfile" >/dev/null 2>&1; then
      return 0
    else
      # Not locked -> stale lockfile
      rm -f "$lockfile" 2>/dev/null
      return 1
    fi
  fi

  # On Unix-like systems
  local pid=""
  pid=$(cat "$lockfile" 2>/dev/null | tr -d '\r')
  pid="${pid#"${pid%%[![:space:]]*}"}"
  pid="${pid%"${pid##*[![:space:]]}"}"

  if [[ -n "$pid" && "$pid" =~ ^[0-9]+$ ]]; then
    if kill -0 "$pid" 2>/dev/null || ps -p "$pid" >/dev/null 2>&1; then
      return 0
    fi
  fi

  if lsof "$lockfile" >/dev/null 2>&1 || fuser "$lockfile" >/dev/null 2>&1; then
    return 0
  fi

  # Stale lockfile
  rm -f "$lockfile" 2>/dev/null
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
  if [ -n "${UNITY_PATH:-}" ] && [ -f "$UNITY_PATH" ]; then
    echo "$UNITY_PATH"
    return 0
  fi

  local version=""
  if [ -f "ProjectSettings/ProjectVersion.txt" ]; then
    version=$(grep "m_EditorVersion:" ProjectSettings/ProjectVersion.txt | awk '{print $2}')
  fi

  if [ -z "$version" ]; then
    version="6000.0.77f1"
  fi

  local paths=(
    "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
    "C:/Program Files/Unity/Hub/Editor/6000.0.77f1/Editor/Unity.exe"
    "C:/Program Files/Unity/Editor/Unity.exe"
  )

  for p in "${paths[@]}"; do
    if [ -f "$p" ]; then
      echo "$p"
      return 0
    fi
  done

  local where_unity=""
  where_unity=$(where unity 2>/dev/null | head -n 1)
  if [ -n "$where_unity" ]; then
    echo "$where_unity"
    return 0
  fi

  return 1
}

# Function to send a command to the socket server
send_socket_cmd() {
  local cmd="$1"
  local timeout="${2:-10}"

  # Read dynamic port
  local port=""
  if [ -f "Temp/unity_cli_port.txt" ]; then
    port=$(cat "Temp/unity_cli_port.txt")
  fi

  if [ -z "$port" ]; then
    return 1
  fi

  local response=""
  # Export command to environment variable to pass to powershell safely without quoting issues
  export UNITY_CLI_CMD="$cmd"
  response=$(powershell -NoProfile -Command "
    \$c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $port);
    \$c.ReceiveTimeout = \$((\$timeout * 1000));
    \$w = New-Object System.IO.StreamWriter(\$c.GetStream());
    \$r = New-Object System.IO.StreamReader(\$c.GetStream());
    \$w.WriteLine(\$env:UNITY_CLI_CMD);
    \$w.Flush();
    \$res = \$r.ReadLine();
    \$c.Close();
    Write-Output \$res;
  " 2>/dev/null)
  local powershell_exit=$?
  
  unset UNITY_CLI_CMD

  if [ $powershell_exit -eq 0 ] && [ -n "$response" ]; then
    # Strip carriage returns and trim whitespace
    response=$(echo "$response" | tr -d '\r')
    response="${response#"${response%%[![:space:]]*}"}"
    response="${response%"${response##*[![:space:]]}"}"
    echo "$response"
    return 0
  fi

  return 1
}

# Function to start background Unity instance or wait for it to be ready
start_background_unity() {
  local mode="${1:-batchmode}"

  local needs_launch=true
  if is_unity_still_running; then
    needs_launch=false
    # Check if already ready
    if [ -f "Temp/unity_cli_port.txt" ] && _=$(send_socket_cmd "PING" 2 2>/dev/null); then
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
      echo "Error: Unity executable not found."
      exit 1
    fi

    echo -n "Starting Unity background instance..."
    mkdir -p Temp
    
    # Run Unity in background (batchmode or interactive)
    local abs_proj_path
    abs_proj_path="$(pwd)"
    if [ "$mode" = "batchmode" ]; then
      "$UNITY_EXE" -batchmode -projectPath "$abs_proj_path" -logFile "Temp/unity_background_log.txt" >/dev/null 2>&1 &
    else
      "$UNITY_EXE" -projectPath "$abs_proj_path" -logFile "Temp/unity_background_log.txt" >/dev/null 2>&1 &
    fi
  fi

  local started=false
  # Wait up to 90 seconds (45 iterations * 2s sleep)
  for i in {1..45}; do
    if [ -f "Temp/unity_cli_port.txt" ]; then
      if _=$(send_socket_cmd "PING" 2 2>/dev/null); then
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
      if ! is_unity_still_running; then
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
  for param in "${EXECUTE_METHOD_PARAMS[@]}"; do
    local escaped="${param//\\/\\\\}"
    escaped="${escaped//\"/\\\"}"
    cmd="$cmd \"$escaped\""
  done

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
      if ! is_unity_still_running; then
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

# Function to parse compilation errors and warnings from a log file
# and print them in dotnet build format.
parse_and_print_compilation_results() {
  local log_file="$1"
  if [ ! -f "$log_file" ]; then
    return 1
  fi

  # Extract lines matching compiler error/warning pattern from the last 1000 lines of the file,
  # and deduplicate preserving order
  local lines
  lines=$(tail -n 1000 "$log_file" | grep -E '^([a-zA-Z]:)?[a-zA-Z0-9_./\\ -]+\([0-9]+,[0-9]+\): (error|warning) [a-zA-Z0-9]+:' | awk '!seen[$0]++')

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

# Clean up stale compilation errors, results, and failures files, and execute files
rm -f Temp/unity_compilation_errors.txt Temp/unity_test_running.txt Temp/unity_test_results.json Temp/unity_test_failures.txt 2>/dev/null
rm -f Temp/unity_execute_result.json Temp/unity_execute_running.txt 2>/dev/null

if [ "$SUBCOMMAND" = "start" ]; then
  start_background_unity "$BG_MODE"
  exit 0

elif [ "$SUBCOMMAND" = "stop" ]; then
  running=false
  if [ "$IS_RUNNING" = true ] || _=$(send_socket_cmd "PING" 2 2>/dev/null); then
    running=true
  fi

  if [ "$running" = false ]; then
    echo "Unity background instance is not running."
    exit 0
  fi

  echo -n "Stopping Unity background instance..."

  stopped=false
  if [ -f "Temp/unity_cli_port.txt" ]; then
    response=$(send_socket_cmd "EXIT" 5 2>/dev/null)
    if [ "$response" = "EXITING" ]; then
      # Wait up to 15 seconds for lockfile to clear
      for i in {1..15}; do
        if [ ! -f "Temp/UnityLockfile" ] && [ ! -f "Temp/UnityLockFile" ]; then
          stopped=true
          break
        fi
        sleep 1
      done
    fi
  fi

  if [ "$stopped" = true ]; then
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

  if [ -n "$lockfile" ]; then
    pid=""
    pid=$(powershell -NoProfile -Command "[System.IO.File]::ReadAllText('$lockfile')" 2>/dev/null | tr -d '\r')
    pid="${pid#"${pid%%[![:space:]]*}"}"
    pid="${pid%"${pid##*[![:space:]]}"}"
    if [ -z "$pid" ]; then
      pid=$(cat "$lockfile" 2>/dev/null)
      pid="${pid#"${pid%%[![:space:]]*}"}"
      pid="${pid%"${pid##*[![:space:]]}"}"
    fi

    if [[ "$pid" =~ ^[0-9]+$ ]]; then
      taskkill //PID "$pid" //F >/dev/null 2>&1 || kill -9 "$pid" >/dev/null 2>&1
    else
      taskkill //IM Unity.exe //F >/dev/null 2>&1
    fi

    # Wait up to 5 seconds for lockfile to disappear
    for i in {1..5}; do
      if [ ! -f "$lockfile" ]; then
        break
      fi
        sleep 1
      done
  fi

  echo ""
  echo "Stopped."
  exit 0
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
  if [ $? -eq 0 ] && [ "$response" = "PONG" ]; then
    echo "Status: Ready"
  else
    echo "Status: Running Unreachable"
  fi
  exit 0
fi

if [ "$IS_RUNNING" = false ]; then
  if [ "$SUBCOMMAND" = "refresh" ] || [ "$SUBCOMMAND" = "test" ] || [ "$SUBCOMMAND" = "executemethod" ]; then
    start_background_unity batchmode
  fi
fi

if [ "$IS_RUNNING" = true ]; then
  if [ "$AUTO_STARTED" = false ]; then
    echo "Detected running Unity instance (via UnityLockfile)."
  fi
  
  # Step 1: Trigger AssetDatabase refresh
  echo -n "Triggering AssetDatabase refresh..."
  while true; do
    if _=$(send_socket_cmd "REFRESH" 2>/dev/null); then
      echo ""
      echo "Done!"
      break
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

if [ "$IS_RUNNING" = true ]; then
  # Step 2: Poll refresh until READY
  echo -n "Waiting for AssetDatabase refresh/compilation to finish..."
  while true; do
    # Sleep 1s
    sleep 1
    
    # Check status. send_socket_cmd reads the port file for each connection attempt.
    response=""
    response=$(send_socket_cmd "POLL_REFRESH" 2)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      if ! is_unity_still_running; then
        echo ""
        echo "Error: Unity background process exited during asset refresh/compilation."
        exit 1
      fi
      # Connection failure (compiling or domain reload in progress)
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
      if [ -f "Temp/unity_compilation_errors.txt" ] && parse_and_print_compilation_results "Temp/unity_compilation_errors.txt"; then
        :
      else
        echo "Error: Unity compilation failed. Check the Unity Editor Console for details."
      fi
      exit 1
    else
      echo -n "."
    fi
  done

  # Step 3: Action Execution
  if [ "$SUBCOMMAND" = "refresh" ]; then
    echo "Refresh completed."
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
