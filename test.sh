#!/usr/bin/env bash
set -u

case "$(uname -s 2>/dev/null || true)" in
  Darwin) PLATFORM=macos ;;
  Linux) PLATFORM=linux ;;
  MINGW*|MSYS*|CYGWIN*) PLATFORM=windows ;;
  *) PLATFORM=unknown ;;
esac

if [ "$PLATFORM" = "unknown" ]; then
  echo "Error: Unsupported host platform. Run test.sh from Bash on Linux, macOS, or Windows Git Bash." >&2
  exit 1
fi

is_windows_platform() {
  [ "$PLATFORM" = "windows" ]
}

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
  local configured_path="${UNITY_PATH:-${UNITY_EDITOR:-}}"
  if [ -n "$configured_path" ]; then
    local shell_configured_path=""
    shell_configured_path=$(to_shell_path "$configured_path" | tr -d '\r')
    if [ -f "$shell_configured_path" ]; then
      printf '%s\n' "$shell_configured_path"
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
    command_unity=$(where.exe unity 2>/dev/null | head -n 1)
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



# Normalization function
normalize_output() {
  local input_file="$1"
  local output_file="$2"
  
  local escaped_proj_path
  escaped_proj_path=$(echo "$abs_proj_path" | sed 's/[].[^$*?+\\|()]/\\&/g')
  
  local escaped_proj_path_win
  if [ -n "${abs_proj_path_win:-}" ]; then
    escaped_proj_path_win=$(echo "$abs_proj_path_win" | sed 's/[].[^$*?+\\|()]/\\&/g')
  else
    escaped_proj_path_win=$(echo "$abs_proj_path" | sed 's/\//\\\\/g' | sed 's/[].[^$*?+\\|()]/\\&/g')
  fi

  sed -E \
    -e 's|\\|/|g' \
    -e '/^DEBUG:/d' \
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
    -e 's|<[a-f0-9]{32}>|<ASSEMBLY_HASH>|g' \
    -e 's|<[a-f0-9]{16,}>|<ASSEMBLY_HASH>|g' \
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
abs_proj_path_win=""
if is_windows_platform; then
  abs_proj_path_win=$(to_native_path "$abs_proj_path" | tr -d '\r')
fi

# Parse CLI arguments for test case filtering
FILTER_PATTERNS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --filter)
      shift
      if [ $# -gt 0 ]; then
        FILTER_PATTERNS+=("$1")
      fi
      ;;
    --filter=*)
      FILTER_PATTERNS+=("${1#*=}")
      ;;
    *)
      FILTER_PATTERNS+=("$1")
      ;;
  esac
  shift
done

matches_filter() {
  local tc="$1"
  if [ ${#FILTER_PATTERNS[@]} -eq 0 ]; then
    return 0
  fi
  for pat in "${FILTER_PATTERNS[@]}"; do
    if [[ "$tc" == "$pat" || "$tc" == *"$pat"* ]]; then
      return 0
    fi
  done
  return 1
}

# Define test cases
TEST_CASES=(
  "TestEverythingPasses"
  "TestCompileErrorsAndWarnings"
  "TestCompileWarningsAndPass"
  "TestNoWarningsAndFailures"
  "TestNoWarningsAndSkipped"
  "TestStopTests"
)

ONLINE_CASES=(
  "${TEST_CASES[@]}"
  "TestExecuteSuccess"
  "TestExecuteFailure"
  "TestExecuteNotFound"
  "TestExecuteCompileError"
  "TestExecuteReturnsInt"
  "TestExecuteReturnsObject"
  "TestExecuteParams"
  "TestEvalSuccess"
  "TestEvalExpression"
  "TestEvalSyntaxError"
  "TestEvalMultiStatement"
  "TestEvalVoidStatement"
  "TestEvalVoidMethod"
  "TestEvalNull"
  "TestEvalDestroyedObject"
  "TestEvalGameObject"
  "TestEvalCollection"
  "TestEvalException"
  "TestFilterCategory"
  "TestBackgroundStatusOnline"
  "TestBackgroundStartAlreadyRunning"
  "TestRecompile"
  "TestRefresh"
  "TestPollRefreshNonBlocking"
)

AUTOSTART_CASES=(
  "TestBackgroundStatusOffline"
  "TestBackgroundStart"
  "TestBackgroundStartAlreadyRunning"
  "TestEvalAutostart"
)

has_matching_cases() {
  local list=("$@")
  for c in "${list[@]}"; do
    if matches_filter "$c"; then
      return 0
    fi
  done
  return 1
}

FAILED_TESTS=0
TOTAL_TESTS_RUN=0

run_integration_case() {
  local tc="$1"
  local cmd_args="$2"
  local mode="$3" # "online" or "offline"

  if ! matches_filter "$tc"; then
    return 0
  fi

  TOTAL_TESTS_RUN=$((TOTAL_TESTS_RUN + 1))
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
      if diff -u --strip-trailing-cr "$expected_file" "$norm_out"; then
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

if has_matching_cases "${ONLINE_CASES[@]}"; then
  echo "============================================="
  echo "PHASE 1: Running integration tests in ONLINE mode"
  echo "============================================="

  # Check if Unity is running
  IS_RUNNING=false
  if bash ./unitycli.sh status 2>/dev/null | grep -q -e "Status: Ready" -e "Status: Running"; then
    IS_RUNNING=true
  fi

  UNITY_EXE=$(find_unity_path)
  if [ -z "$UNITY_EXE" ]; then
    echo "Error: Unity executable not found for Unity version $(grep "m_EditorVersion:" ProjectSettings/ProjectVersion.txt | awk '{print $2}' | tr -d '\r')."
    exit 1
  fi

  run_setup "online"
  if [ "$IS_RUNNING" = false ]; then
    bash ./unitycli.sh start batchmode
  else
    echo "Unity is already running."
  fi

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

  # eval tests (online)
  run_integration_case "TestEvalSuccess" "eval 1 + 1" "online"
  run_integration_case "TestEvalExpression" "eval Mathf.Sqrt(16f)" "online"
  run_integration_case "TestEvalSyntaxError" "eval this is invalid syntax @@" "online"
  run_integration_case "TestEvalMultiStatement" "eval int a = 10; int b = 20; return a + b;" "online"
  run_integration_case "TestEvalVoidStatement" "eval UnityEngine.Debug.Log(42);" "online"
  run_integration_case "TestEvalVoidMethod" "eval System.GC.Collect()" "online"
  run_integration_case "TestEvalNull" "eval (object)null" "online"
  run_integration_case "TestEvalDestroyedObject" "eval GameObject go = new GameObject(\"TempObj\"); GameObject.DestroyImmediate(go); return go;" "online"
  run_integration_case "TestEvalGameObject" "eval new GameObject(\"SampleEntity\")" "online"
  run_integration_case "TestEvalCollection" "eval new int[] { 10, 20, 30 }" "online"
  run_integration_case "TestEvalException" "eval throw new System.InvalidOperationException(\"test-eval-error\");" "online"

  # filter test (online)
  run_integration_case "TestFilterCategory" "test --editmode --category !LongRunning" "online"

  # status/start tests (online)
  run_integration_case "TestBackgroundStatusOnline" "status" "online"
  run_integration_case "TestBackgroundStartAlreadyRunning" "start batchmode" "online"

  # refresh/recompile tests (online)
  run_integration_case "TestRefresh" "refresh" "online"
  run_integration_case "TestPollRefreshNonBlocking" "executemethod Tests.DummyExecuteClass.PollRefreshWhileBusy" "online"
  run_integration_case "TestRecompile" "recompile" "online"

  # Close Unity
  run_teardown "online"
  bash ./unitycli.sh stop
fi

if has_matching_cases "${AUTOSTART_CASES[@]}"; then
  echo "============================================="
  echo "PHASE 2: Running integration tests for AUTO-START"
  echo "============================================="

  # 1. Start with stopped Unity. Run status (should be Not Running).
  run_integration_case "TestBackgroundStatusOffline" "status" "autostart"

  # 2. Run start batchmode when stopped (should start and wait).
  run_integration_case "TestBackgroundStart" "start batchmode" "autostart"

  # 3. Run start batchmode when already running (should say Unity is already running).
  run_integration_case "TestBackgroundStartAlreadyRunning" "start batchmode" "autostart"

  # 4. Stop Unity before testing auto-start eval from stopped state.
  bash ./unitycli.sh stop

  # 5. Run eval when stopped (should auto-start Unity and evaluate).
  run_integration_case "TestEvalAutostart" "eval 2 + 2" "autostart"

  # 6. Stop Unity.
  bash ./unitycli.sh stop
fi

echo "============================================="
if [ $TOTAL_TESTS_RUN -eq 0 ]; then
  echo "WARNING: No tests matched filter pattern(s): ${FILTER_PATTERNS[*]}"
fi
if [ $FAILED_TESTS -eq 0 ]; then
  echo "ALL INTEGRATION TESTS PASSED SUCCESSFULLY! ($TOTAL_TESTS_RUN test(s) run)"
  exit 0
else
  echo "INTEGRATION TESTS FAILED: $FAILED_TESTS failure(s) out of $TOTAL_TESTS_RUN test(s) run"
  exit 1
fi
