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
MODE_PLAYMODE=false
MODE_EDITMODE=false
FILTER=""

# Helper for usage
show_usage() {
  echo "Usage: $0 [--playmode] [--editmode] [--filter <filter>]"
  echo "  --playmode          Run PlayMode tests"
  echo "  --editmode          Run EditMode tests"
  echo "  --filter <filter>   Filter tests by name (regex/substring)"
  exit 1
}

# Parse command line arguments
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
    -h|--help)
      show_usage
      ;;
    *)
      echo "Unknown option: $1"
      show_usage
      ;;
  esac
done

# If neither mode is specified, default to running both
if [ "$MODE_PLAYMODE" = false ] && [ "$MODE_EDITMODE" = false ]; then
  MODE_PLAYMODE=true
  MODE_EDITMODE=true
fi

# Detect if Unity is running for this specific project
IS_RUNNING=false
if [ -f "Temp/UnityLockfile" ] || [ -f "Temp/UnityLockFile" ]; then
  # Try to delete the lockfile. If Unity is running, it holds an exclusive lock
  # and the OS will prevent deletion. If Unity is not running, the delete will succeed.
  if rm "Temp/UnityLockfile" 2>/dev/null || rm "Temp/UnityLockFile" 2>/dev/null; then
    IS_RUNNING=false
  else
    IS_RUNNING=true
  fi
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
  # Query using PowerShell to avoid Git Bash /dev/tcp subshell inheritance limitations.
  # We use single quotes for the PowerShell code block to avoid bash variable expansion.
  response=$(powershell -NoProfile -Command "
    \$c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $port);
    \$c.ReceiveTimeout = $(($timeout * 1000));
    \$w = New-Object System.IO.StreamWriter(\$c.GetStream());
    \$r = New-Object System.IO.StreamReader(\$c.GetStream());
    \$w.WriteLine('$cmd');
    \$w.Flush();
    \$res = \$r.ReadLine();
    \$c.Close();
    Write-Output \$res;
  " 2>/dev/null)

  if [ $? -eq 0 ] && [ -n "$response" ]; then
    # Strip carriage returns and trim whitespace
    response=$(echo "$response" | tr -d '\r' | xargs)
    echo "$response"
    return 0
  fi

  return 1
}

# Function to run tests via socket (Online)
run_online_tests() {
  local mode="$1"
  echo "Sending command to run $mode tests..."

  local response=""
  # Trigger test execution. The server responds "RUNNING" immediately and closes the socket.
  # If a domain reload begins instantly, the connection might close without response, which we also treat as started.
  response=$(send_socket_cmd "RUN_TESTS $mode $FILTER" 10)
  
  echo -n "Waiting for tests to complete"
  while true; do
    sleep 1

    # Re-read port/query status. The connection will fail during domain reloads, which is expected.
    response=$(send_socket_cmd "POLL_TESTS" 5)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      echo -n "."
      continue
    fi

    if [ "$response" = "RUNNING" ]; then
      echo -n "."
    elif [[ "$response" == SUCCESS* ]]; then
      echo " Done!"
      echo "Unity Response: $response"
      return 0
    elif [[ "$response" == FAILURE* ]]; then
      echo " Done!"
      echo "Unity Response: $response"
      return 1
    else
      # If IDLE or ERROR
      echo " Done!"
      echo "Unity Response: $response"
      return 2
    fi
  done
}

# Function to run tests in batchmode (Offline)
run_offline_tests() {
  local mode="$1"
  local platform="EditMode"
  if [ "$mode" = "playmode" ]; then
    platform="PlayMode"
  fi

  echo "Running $mode tests in batchmode..."
  
  # Local relative paths for bash operations
  local bash_results_file="Temp/test-results-${mode}.xml"
  local bash_log_file="Temp/unity_batch_log.txt"

  rm -f "$bash_results_file"
  rm -f "$bash_log_file"

  # Use absolute paths with forward slashes for Windows Unity.exe execution.
  # This avoids any backslash-escaping issues when passing args from bash to Windows.
  local abs_proj_path="$(pwd)"
  local abs_results_file="$(pwd)/$bash_results_file"
  local abs_log_file="$(pwd)/$bash_log_file"

  mkdir -p Temp

  local args=(-batchmode -runTests -projectPath "$abs_proj_path" -testPlatform "$platform" -testResults "$abs_results_file" -logFile "$abs_log_file")
  if [ -n "$FILTER" ]; then
    args+=(-testFilter "$FILTER")
  fi

  # Run Unity
  "$UNITY_EXE" "${args[@]}"
  local unity_exit=$?

  if [ $unity_exit -eq 0 ]; then
    echo "Unity Response: SUCCESS"
    return 0
  else
    echo "Unity Response: FAILURE"
    if [ -f "$bash_log_file" ]; then
      echo "------------------------------------------------------------"
      echo "Last 50 lines of Unity batch log ($bash_log_file):"
      echo "------------------------------------------------------------"
      tail -n 50 "$bash_log_file"
      echo "------------------------------------------------------------"
    fi
    return 1
  fi
}

# --- Main Execution ---

if [ "$IS_RUNNING" = true ]; then
  echo "Detected running Unity instance (via UnityLockfile)."
  
  # Step 1: Trigger AssetDatabase refresh
  echo -n "Triggering AssetDatabase refresh..."
  while true; do
    if send_socket_cmd "REFRESH" >/dev/null; then
      echo " Done!"
      break
    fi
    echo -n "."
    sleep 1
  done
fi

if [ "$IS_RUNNING" = true ]; then
  # Step 2: Poll refresh until READY
  echo -n "Waiting for AssetDatabase refresh/compilation to finish"
  while true; do
    # Sleep 1s
    sleep 1
    
    # Read port again in case it changed due to reload
    current_port=""
    if [ -f "Temp/unity_cli_port.txt" ]; then
      current_port=$(cat "Temp/unity_cli_port.txt")
    fi
    
    if [ -z "$current_port" ]; then
      echo -n "."
      continue
    fi
    
    # Check status
    response=""
    response=$(send_socket_cmd "POLL_REFRESH" 2)
    if [ $? -ne 0 ] || [ -z "$response" ]; then
      # Connection failure (compiling or domain reload in progress)
      echo -n "."
      continue
    fi
    
    if [ "$response" = "READY" ]; then
      echo " Unity is ready!"
      break
    elif [ "$response" = "COMPILATION_ERROR" ]; then
      echo ""
      echo "Error: Unity compilation failed. Check the Unity Editor Console for details."
      exit 1
    else
      echo -n "."
    fi
  done

  # Step 3: Run online tests
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

else
  echo "Unity is not running. Running in batchmode..."
  
  UNITY_EXE=$(find_unity_path)
  if [ -z "$UNITY_EXE" ]; then
    echo "Error: Unity executable not found."
    exit 1
  fi
  echo "Found Unity at: $UNITY_EXE"

  TESTS_FAILED=false
  if [ "$MODE_EDITMODE" = true ]; then
    run_offline_tests "editmode"
    if [ $? -ne 0 ]; then
      TESTS_FAILED=true
    fi
  fi

  if [ "$MODE_PLAYMODE" = true ]; then
    run_offline_tests "playmode"
    if [ $? -ne 0 ]; then
      TESTS_FAILED=true
    fi
  fi

  if [ "$TESTS_FAILED" = true ]; then
    echo "Some batchmode tests failed."
    exit 1
  else
    echo "All batchmode tests passed."
    exit 0
  fi
fi
