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

  local is_windows=false
  if [[ "${OSTYPE:-}" == "msys" || "${OSTYPE:-}" == "cygwin" || "${OSTYPE:-}" == "mingw"* || "${OS:-}" == "Windows_NT" ]]; then
    is_windows=true
  fi

  local paths=()
  if [ "$is_windows" = true ]; then
    paths=(
      "C:/Program Files/Unity/Hub/Editor/$version/Editor/Unity.exe"
      "C:/Program Files/Unity/Hub/Editor/6000.0.77f1/Editor/Unity.exe"
      "C:/Program Files/Unity/Editor/Unity.exe"
    )
  elif [[ "$(uname)" == "Darwin" ]]; then
    paths=(
      "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
      "/Applications/Unity/Hub/Editor/6000.0.77f1/Unity.app/Contents/MacOS/Unity"
      "/Applications/Unity/Unity.app/Contents/MacOS/Unity"
    )
  else
    paths=(
      "$HOME/Unity/Hub/Editor/$version/Editor/Unity"
      "$HOME/Unity/Hub/Editor/6000.0.77f1/Editor/Unity"
      "/opt/unity/Editor/Unity"
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
    command_unity=$(where unity 2>/dev/null | head -n 1)
  else
    command_unity=$(command -v unity 2>/dev/null)
  fi

  if [ -n "$command_unity" ]; then
    echo "$command_unity"
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
  if [[ "${OSTYPE:-}" == "msys" || "${OSTYPE:-}" == "cygwin" || "${OSTYPE:-}" == "mingw"* || "${OS:-}" == "Windows_NT" ]]; then
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
    if [ $powershell_exit -ne 0 ] || [ -z "$response" ]; then
      return 1
    fi
  else
    # Non-Windows (macOS, Linux)
    if command -v nc >/dev/null 2>&1; then
      response=$(echo "$cmd" | nc -w "$timeout" 127.0.0.1 "$port" 2>/dev/null | head -n 1)
    elif (echo >/dev/tcp/127.0.0.1/$port) >/dev/null 2>&1; then
      exec 3<>/dev/tcp/127.0.0.1/$port
      echo "$cmd" >&3
      if read -t "$timeout" response <&3; then
        response=$(echo "$response" | head -n 1)
      fi
      exec 3>&-
    fi
    if [ -z "$response" ]; then
      return 1
    fi
  fi

  # Strip carriage returns and trim whitespace
  response=$(echo "$response" | tr -d '\r')
  response="${response#"${response%%[![:space:]]*}"}"
  response="${response%"${response##*[![:space:]]}"}"
  echo "$response"
  return 0
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
    -e 's|\x1B\[([0-9]{1,2}(;[0-9]{1,2})?)?[mGK]||g' \
    -e "s|$escaped_proj_path|PROJECT_PATH|gI" \
    -e "s|$escaped_proj_path_win|PROJECT_PATH|gI" \
    -e 's|\[[0-9]+ ms\]|[DURATION]|g' \
    -e 's|\[< 1 ms\]|[DURATION]|g' \
    -e 's|Waiting for tests to complete\.*$|Waiting for tests to complete...|g' \
    -e 's|Waiting for AssetDatabase refresh/compilation to finish\.*$|Waiting for AssetDatabase refresh/compilation to finish...|g' \
    -e 's|Triggering AssetDatabase refresh\.*$|Triggering AssetDatabase refresh...|g' \
    -e 's|Waiting for recompilation to finish\.*$|Waiting for recompilation to finish...|g' \
    -e 's|Triggering force recompilation\.*$|Triggering force recompilation...|g' \
    -e 's|Waiting for method execution to complete\.*$|Waiting for method execution to complete...|g' \
    -e 's|Connecting\.*$|Connecting...|g' \
    -e 's|Starting Unity background instance\.*$|Starting Unity background instance...|g' \
    -e 's|Waiting for Unity background instance to be ready\.*$|Waiting for Unity background instance to be ready...|g' \
    -e 's|^Stopping Unity background instance\.*$|Stopping Unity background instance...|g' \
    -e 's|Found Unity at: .*|Found Unity at: UNITY_EXE|g' \
    -e 's|PROJECT_PATH\\|PROJECT_PATH/|g' \
    -e 's|c:/program files/unity/hub/editor/[^/]+/editor/unity.exe|UNITY_EXE|gI' \
    -e 's|c:\\program files\\unity\\hub\\editor\\[^\\]+\\editor\\unity.exe|UNITY_EXE|gI' \
    -e 's|id=[a-f0-9]+|id=ASSET_DB_ID|g' \
    -e 's|[0-9.]+[[:space:]]*ms|DURATIONms|g' \
    -e 's|[0-9.]+[[:space:]]*seconds|DURATIONseconds|g' \
    -e 's|[0-9.]+ (MB\|KB\|GB)|SIZE_MB|g' \
    -e 's|Unloading [0-9]+ unused Assets|Unloading UNUSED_ASSETS unused Assets|g' \
    -e 's|Loaded Objects now: [0-9]+|Loaded Objects now: LOADED_OBJECTS|g' \
    -e 's|##utp:\{.*\}|##utp:JSON|g' \
    -e 's|Scanning for USB devices : USB_DURATIONms|Scanning for USB devices : USB_DURATION|g' \
    -e '/Cleanup mono/d' \
    -e '/\[MODES\]/d' \
    -e '/Shut down\./d' \
    -e '/Physics::Module/d' \
    -e '/Input System module/d' \
    -e '/Input System polling thread/d' \
    -e '/Licensing::IpcConnector/d' \
    -e '/AcceleratorClientConnectionCallback/d' \
    -e '/RiderPlugin/d' \
    -e '/ThreadAbortException/d' \
    -e '/Accept_icall/d' \
    -e '/Accept_internal/d' \
    -e '/Socket\.Accept/d' \
    -e '/TcpListener\.AcceptTcpClient/d' \
    -e '/UnityCliServer\.ServerLoop/d' \
    -e '/abort_threads/d' \
    -e '/debugger-agent/d' \
    -e '/Curl error 42/d' \
    -e '/Scanning for USB devices/d' \
    -e '/Initializing Unity extensions/d' \
    -e '/will not be compiled because it exists outside the Assets folder/d' \
    -e '/UnityEngine.Debug/d' \
    -e '/UnityEngine.StackTraceUtility/d' \
    -e '/UnityEngine.DebugLogHandler/d' \
    -e '/UnityEngine.Logger/d' \
    -e '/UnityCliRunner\.UnityCliServer:ExecuteMethod/d' \
    -e '/Filename: .*UnityCliServer.cs/d' \
    -e 's|UnityCliServer\.cs:[0-9]+|UnityCliServer.cs:LINE|g' \
    -e 's|UnityCliServer\.cs Line: [0-9]+|UnityCliServer.cs Line: LINE|g' \
    -e 's|\r||g' \
    "$input_file" > "$output_file"
}

run_setup() {
  local phase="$1"
  if [ -f "IntegrationTests/setup.sh" ]; then
    echo "Running global setup..."
    chmod +x "IntegrationTests/setup.sh" 2>/dev/null || true
    ./IntegrationTests/setup.sh
  fi
  if [ -f "IntegrationTests/setup.${phase}.sh" ]; then
    echo "Running ${phase} setup..."
    chmod +x "IntegrationTests/setup.${phase}.sh" 2>/dev/null || true
    ./IntegrationTests/setup.${phase}.sh
  fi
}

run_teardown() {
  local phase="$1"
  if [ -f "IntegrationTests/teardown.${phase}.sh" ]; then
    echo "Running ${phase} teardown..."
    chmod +x "IntegrationTests/teardown.${phase}.sh" 2>/dev/null || true
    ./IntegrationTests/teardown.${phase}.sh
  fi
  if [ -f "IntegrationTests/teardown.sh" ]; then
    echo "Running global teardown..."
    chmod +x "IntegrationTests/teardown.sh" 2>/dev/null || true
    ./IntegrationTests/teardown.sh
  fi
}

abs_proj_path="$(pwd)"

# Check if Unity is running
IS_RUNNING=false
if bash ./unitycli.sh status 2>/dev/null | grep -q -e "Status: Ready" -e "Status: Running"; then
  IS_RUNNING=true
fi

UNITY_EXE=$(find_unity_path)
if [ -z "$UNITY_EXE" ]; then
  echo "Error: Unity executable not found."
  exit 1
fi

run_setup "online"
if [ "$IS_RUNNING" = false ]; then
  bash ./unitycli.sh start batchmode
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

run_integration_case() {
  local tc="$1"
  local cmd_args="$2"
  local mode="$3" # "online" or "offline"

  echo "--- Running test case: $tc ($mode) with command: bash ./unitycli.sh $cmd_args ---"
  
  if [ -f "IntegrationTests/$tc/DummyTest.cs" ]; then
    cp "IntegrationTests/$tc/DummyTest.cs" "$DUMMY_TEST_PATH"
    # Sleep to allow Unity to detect compilation change in online mode
    if [ "$mode" = "online" ]; then
      sleep 2
    fi
  fi
  
  local raw_out="IntegrationTests/Temp/raw_out_${mode}.txt"
  local norm_out="IntegrationTests/Temp/norm_out_${mode}.txt"
  rm -f "$raw_out" "$norm_out"
  
  bash ./unitycli.sh $cmd_args > "$raw_out" 2>&1
  local exit_code=$?
  
  echo "EXIT_CODE: $exit_code" >> "$raw_out"
  
  normalize_output "$raw_out" "$norm_out"
  
  local expected_file="IntegrationTests/$tc/output.${mode}.verified.txt"
  local received_file="IntegrationTests/$tc/output.${mode}.received.txt"
  
  if [ "${BOOTSTRAP:-false}" = "true" ]; then
    cp "$norm_out" "$expected_file"
    rm -f "$received_file"
    echo "Bootstrapped $expected_file"
  else
    if [ ! -f "$expected_file" ]; then
      echo "Error: Expected file $expected_file does not exist. Run with BOOTSTRAP=true to generate."
      cp "$norm_out" "$received_file"
      FAILED_TESTS=$((FAILED_TESTS + 1))
    else
      if diff -u "$expected_file" "$norm_out"; then
        echo "SUCCESS: Output matches $expected_file"
        rm -f "$received_file"
      else
        echo "FAILURE: Output does not match $expected_file"
        echo "Raw output was:"
        cat "$raw_out"
        echo "Normalized output was:"
        cat "$norm_out"
        cp "$norm_out" "$received_file"
        FAILED_TESTS=$((FAILED_TESTS + 1))
      fi
    fi
  fi
}

echo "============================================="
echo "PHASE 1: Running integration tests in ONLINE mode"
echo "============================================="

for tc in "${TEST_CASES[@]}"; do
  run_integration_case "$tc" "test --editmode" "online"
done

# executemethod tests (online)
run_integration_case "TestExecuteSuccess" "executemethod Tests.DummyExecuteClass.SuccessMethod" "online"
run_integration_case "TestExecuteFailure" "executemethod Tests.DummyExecuteClass.FailMethod" "online"
run_integration_case "TestExecuteNotFound" "executemethod Tests.DummyExecuteClass.NonExistentMethod" "online"
run_integration_case "TestExecuteCompileError" "executemethod Tests.DummyExecuteClass.SuccessMethod" "online"
run_integration_case "TestExecuteReturnsInt" "executemethod Tests.DummyExecuteClass.Something" "online"
run_integration_case "TestExecuteReturnsObject" "executemethod Tests.DummyExecuteClass.Something" "online"
run_integration_case "TestExecuteParams" "executemethod Tests.DummyExecuteClass.ParamsMethod 4 3.5 hello {\"Value\":42}" "online"

# filter test (online)
run_integration_case "TestFilterCategory" "test --editmode --category !LongRunning" "online"

# status/start tests (online)
run_integration_case "TestBackgroundStatusOnline" "status" "online"
run_integration_case "TestBackgroundStartAlreadyRunning" "start batchmode" "online"

# recompile tests (online)
run_integration_case "TestRecompile" "recompile" "online"

# Close Unity
run_teardown "online"
bash ./unitycli.sh stop

echo "============================================="
echo "PHASE 2: Running integration tests for AUTO-START"
echo "============================================="

# 1. Start with stopped Unity. Run status (should be Not Running).
run_integration_case "TestBackgroundStatusOffline" "status" "autostart"

# 2. Run start batchmode when stopped (should start and wait).
run_integration_case "TestBackgroundStart" "start batchmode" "autostart"

# 3. Run start batchmode when already running (should say Unity is already running).
run_integration_case "TestBackgroundStartAlreadyRunning" "start batchmode" "autostart"

# 4. Stop Unity.
bash ./unitycli.sh stop

# 5. Run test when stopped (should auto-start and run test).
run_integration_case "TestEverythingPasses" "test --editmode" "autostart"

# 6. Stop Unity.
bash ./unitycli.sh stop

# 7. Run executemethod when stopped (should auto-start and execute).
run_integration_case "TestExecuteSuccess" "executemethod Tests.DummyExecuteClass.SuccessMethod" "autostart"

# 8. Stop Unity.
bash ./unitycli.sh stop

# 9. Run recompile when stopped (should auto-start and recompile).
run_integration_case "TestRecompile" "recompile" "autostart"

# 10. Stop Unity.
bash ./unitycli.sh stop

echo "============================================="
if [ $FAILED_TESTS -eq 0 ]; then
  echo "ALL INTEGRATION TESTS PASSED SUCCESSFULLY!"
  exit 0
else
  echo "INTEGRATION TESTS FAILED: $FAILED_TESTS failure(s)"
  exit 1
fi
