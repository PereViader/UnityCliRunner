#!/usr/bin/env bash
set -u

# Backup directory
BACKUP_DIR="IntegrationTests/Backup"
mkdir -p "$BACKUP_DIR"

# Test logs temp directory
mkdir -p "IntegrationTests/Temp"

DUMMY_TEST_PATH="Assets/Tests/Editor/DummyTest.cs"
DUMMY_TEST_META_PATH="Assets/Tests/Editor/DummyTest.cs.meta"

# Back up original files
if [ -f "$DUMMY_TEST_PATH" ]; then
  cp "$DUMMY_TEST_PATH" "$BACKUP_DIR/DummyTest.cs"
fi
if [ -f "$DUMMY_TEST_META_PATH" ]; then
  cp "$DUMMY_TEST_META_PATH" "$BACKUP_DIR/DummyTest.cs.meta"
fi

restore_backup() {
  echo "Restoring original DummyTest..."
  if [ -f "$BACKUP_DIR/DummyTest.cs" ]; then
    cp "$BACKUP_DIR/DummyTest.cs" "$DUMMY_TEST_PATH"
  else
    rm -f "$DUMMY_TEST_PATH"
  fi
  
  if [ -f "$BACKUP_DIR/DummyTest.cs.meta" ]; then
    cp "$BACKUP_DIR/DummyTest.cs.meta" "$DUMMY_TEST_META_PATH"
  else
    rm -f "$DUMMY_TEST_META_PATH"
  fi
  rm -rf "$BACKUP_DIR"
  rm -rf "IntegrationTests/Temp"
}
trap restore_backup EXIT INT TERM

# Helper function to find Unity path
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

# Helper to send command to the socket server
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
    response=$(echo "$response" | tr -d '\r')
    response="${response#"${response%%[![:space:]]*}"}"
    response="${response%"${response##*[![:space:]]}"}"
    echo "$response"
    return 0
  fi

  return 1
}

# Normalization function
normalize_output() {
  local input_file="$1"
  local output_file="$2"
  
  local escaped_proj_path
  escaped_proj_path=$(echo "$abs_proj_path" | sed 's/[].[^$*?+\\|()]/\\&/g')
  
  local escaped_proj_path_win
  escaped_proj_path_win=$(echo "$abs_proj_path" | sed 's/\//\\\\/g' | sed 's/[].[^$*?+\\|()]/\\&/g')

  sed -E \
    -e 's/\x1B\[([0-9]{1,2}(;[0-9]{1,2})?)?[mGK]//g' \
    -e "s|$escaped_proj_path|PROJECT_PATH|gI" \
    -e "s|$escaped_proj_path_win|PROJECT_PATH|gI" \
    -e 's/\[[0-9]+ ms\]/[DURATION]/g' \
    -e 's/\[< 1 ms\]/[DURATION]/g' \
    -e 's/Waiting for tests to complete\.* Done!/Waiting for tests to complete. Done!/g' \
    -e 's/Waiting for AssetDatabase refresh\/compilation to finish\.* Unity is ready!/Waiting for AssetDatabase refresh\/compilation to finish. Unity is ready!/g' \
    -e 's/Triggering AssetDatabase refresh\.* Done!/Triggering AssetDatabase refresh. Done!/g' \
    -e 's|Found Unity at: .*|Found Unity at: UNITY_EXE|g' \
    -e 's|PROJECT_PATH\\|PROJECT_PATH/|g' \
    -e 's|c:/program files/unity/hub/editor/[^/]+/editor/unity.exe|UNITY_EXE|gI' \
    -e 's|c:\\program files\\unity\\hub\\editor\\[^\\]+\\editor\\unity.exe|UNITY_EXE|gI' \
    -e 's/\r//g' \
    "$input_file" > "$output_file"
}

abs_proj_path="$(pwd)"

# Check if Unity is running
IS_RUNNING=false
if [ -f "Temp/UnityLockfile" ] || [ -f "Temp/UnityLockFile" ]; then
  if rm "Temp/UnityLockfile" 2>/dev/null || rm "Temp/UnityLockFile" 2>/dev/null; then
    IS_RUNNING=false
  else
    IS_RUNNING=true
  fi
fi

UNITY_EXE=$(find_unity_path)
if [ -z "$UNITY_EXE" ]; then
  echo "Error: Unity executable not found."
  exit 1
fi

if [ "$IS_RUNNING" = false ]; then
  echo "Unity is not running. Launching Unity in the background..."
  "$UNITY_EXE" -projectPath "$abs_proj_path" >/dev/null 2>&1 &
  
  # Wait for Unity to start and register the TCP port
  echo "Waiting for Unity CLI server to start..."
  started=false
  for i in {1..90}; do
    if [ -f "Temp/unity_cli_port.txt" ]; then
      if send_socket_cmd "POLL_REFRESH" 2 >/dev/null 2>&1; then
        echo "Unity is ready and socket server is running."
        started=true
        break
      fi
    fi
    echo -n "."
    sleep 2
  done
  echo ""
  if [ "$started" = false ]; then
    echo "Error: Unity failed to start or TCP server did not become ready in time."
    exit 1
  fi
else
  echo "Unity is already running."
fi

# Define test cases
TEST_CASES=(
  "TestEverythingPasses"
  "TestCompileErrorsAndWarnings"
  "TestCompileWarningsAndPass"
  "TestNoWarningsAndFailures"
  "TestNoWarningsAndSkipped"
)

FAILED_TESTS=0

echo "============================================="
echo "PHASE 1: Running integration tests in ONLINE mode"
echo "============================================="

for tc in "${TEST_CASES[@]}"; do
  echo "--- Running test case: $tc (Online) ---"
  
  cp "IntegrationTests/$tc/DummyTest.cs" "$DUMMY_TEST_PATH"
  sleep 2
  
  raw_out="IntegrationTests/Temp/raw_out_online.txt"
  norm_out="IntegrationTests/Temp/norm_out_online.txt"
  rm -f "$raw_out" "$norm_out"
  
  ./run_tests.sh --editmode > "$raw_out" 2>&1
  exit_code=$?
  
  echo "EXIT_CODE: $exit_code" >> "$raw_out"
  
  normalize_output "$raw_out" "$norm_out"
  
  expected_file="IntegrationTests/$tc/ExpectedOutput_Online.txt"
  
  if [ "${BOOTSTRAP:-false}" = "true" ]; then
    cp "$norm_out" "$expected_file"
    echo "Bootstrapped $expected_file"
  else
    if [ ! -f "$expected_file" ]; then
      echo "Error: Expected file $expected_file does not exist. Run with BOOTSTRAP=true to generate."
      FAILED_TESTS=$((FAILED_TESTS + 1))
    else
      if diff -u "$expected_file" "$norm_out"; then
        echo "SUCCESS: Output matches $expected_file"
      else
        echo "FAILURE: Output does not match $expected_file"
        echo "Raw output was:"
        cat "$raw_out"
        echo "Normalized output was:"
        cat "$norm_out"
        FAILED_TESTS=$((FAILED_TESTS + 1))
      fi
    fi
  fi
done

# Close Unity
echo "Closing Unity..."
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
    echo "Stopping Unity editor (PID: $pid)..."
    taskkill //PID "$pid" //F >/dev/null 2>&1 || kill -9 "$pid" >/dev/null 2>&1
  else
    echo "Stopping Unity editor by process name..."
    taskkill //IM Unity.exe //F >/dev/null 2>&1
  fi
  
  echo "Waiting for Unity lockfile to disappear..."
  for i in {1..30}; do
    if [ ! -f "$lockfile" ]; then
      break
    fi
    sleep 1
  done
  sleep 3
fi

echo "============================================="
echo "PHASE 2: Running integration tests in OFFLINE mode"
echo "============================================="

for tc in "${TEST_CASES[@]}"; do
  echo "--- Running test case: $tc (Offline) ---"
  
  cp "IntegrationTests/$tc/DummyTest.cs" "$DUMMY_TEST_PATH"
  sleep 2
  
  raw_out="IntegrationTests/Temp/raw_out_offline.txt"
  norm_out="IntegrationTests/Temp/norm_out_offline.txt"
  rm -f "$raw_out" "$norm_out"
  
  ./run_tests.sh --editmode > "$raw_out" 2>&1
  exit_code=$?
  
  echo "EXIT_CODE: $exit_code" >> "$raw_out"
  
  normalize_output "$raw_out" "$norm_out"
  
  expected_file="IntegrationTests/$tc/ExpectedOutput_Offline.txt"
  
  if [ "${BOOTSTRAP:-false}" = "true" ]; then
    cp "$norm_out" "$expected_file"
    echo "Bootstrapped $expected_file"
  else
    if [ ! -f "$expected_file" ]; then
      echo "Error: Expected file $expected_file does not exist. Run with BOOTSTRAP=true to generate."
      FAILED_TESTS=$((FAILED_TESTS + 1))
    else
      if diff -u "$expected_file" "$norm_out"; then
        echo "SUCCESS: Output matches $expected_file"
      else
        echo "FAILURE: Output does not match $expected_file"
        echo "Raw output was:"
        cat "$raw_out"
        echo "Normalized output was:"
        cat "$norm_out"
        FAILED_TESTS=$((FAILED_TESTS + 1))
      fi
    fi
  fi
done

echo "============================================="
if [ $FAILED_TESTS -eq 0 ]; then
  echo "ALL INTEGRATION TESTS PASSED SUCCESSFULLY!"
  exit 0
else
  echo "INTEGRATION TESTS FAILED: $FAILED_TESTS failure(s)"
  exit 1
fi
