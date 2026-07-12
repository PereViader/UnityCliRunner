[![Test and publish](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityCliRunner?include_prereleases)](https://github.com/PereViader/UnityCliRunner/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityclirunner?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityclirunner/)


# UnityCliRunner

A lightweight command-line runner that bridges your shell and AI coding agents with the Unity Editor.

### Key Capabilities

* **Trigger Asset Database Refresh**: Instantly recompile your C# code and feed any compilation errors or warnings directly back to your terminal or AI coding agent.
* **Run Editor & PlayMode Tests**: Execute unit and integration tests seamlessly as part of your feature implementation workflow.
* **Run Static Methods**: Execute any C# static method with support for primitive parameters and JSON deserialization.

---

## Installation & Setup

### 1. Requirements
- **macOS / Linux**: A terminal with Bash (pre-installed on almost all distributions).
- **Windows**: Git Bash (included with Git for Windows).
- **Unity**: Version 2021.3 or higher.

### 2. Install the Package

[Install from on OpenUPM](https://openupm.com/packages/com.pereviader.unityclirunner/#modal-manualinstallation).

### 3. Install unitycli.sh and Agent Skill

In the Unity Editor, run the installers from the top menu dropdown:
- **Tools > UnityCliRunner > InstallBashScript** to copy the runner script (`unitycli.sh`) to the root of your Unity project.
- **Tools > UnityCliRunner > InstallSkill** to copy the `.agents/skills/unity-cli` folder to the root of your project (required if you use agentic AI tools like **Antigravity**, **Gemini**, **Cline**, or **Roo Code**).

Note: If the root of the unity project is not the root of your repository you may want to move the `.agents` folder to the root of the repository.



---

## AI Agent Integration & Agent Skills

If you use agentic AI tools (like **Antigravity**, **Gemini**, **Claude**, ...), UnityCliRunner includes a pre-packaged **Agent Skill** under `.agents/skills/unity-cli`
### Benefits of the Agent Skill:
- **Sub-Second Feedback**: Agents compile code and run tests instantly, avoiding slow batchmode restarts (~30s delay).
- **Background Unity Process**: Keeps a headless Unity process open in the background so it can be reused for quick iterations.
- **Diagnostics Formatting**: Compilation errors and test failures are formatted in standard compiler patterns, making it easy for agents to parse and resolve them autonomously.

To install, select **Tools > UnityCliRunner > InstallSkill** in the Unity Editor to copy the `.agents/` folder to your project root.

---

## How to Use It

Run `unitycli.sh` from the root directory of your Unity project:

### Background Instance Management
Keep a background Unity instance warm to execute tests and methods in sub-second Online Mode without launching the Unity GUI:

```bash
# Start a background Unity instance in headless batchmode
# (If already starting or running, it blocks and waits until it is ready)
bash unitycli.sh start batchmode

# Start a background Unity instance in interactive mode (opens Unity Editor GUI)
bash unitycli.sh start interactive

# Check if the background Unity instance is running and reachable
bash unitycli.sh status

# Safely stop the background Unity instance (falls back to process kill if needed)
bash unitycli.sh stop
```

### Core Operations
If Unity is not running, these commands will automatically start a background Unity instance in batchmode first before proceeding with the execution:

```bash
# Trigger AssetDatabase.Refresh() and print compilation diagnostics
bash unitycli.sh refresh

# Force a full C# recompilation (clean build cache) and print compiler diagnostics
bash unitycli.sh recompile

# Run both EditMode and PlayMode tests
bash unitycli.sh test

# Run only EditMode tests
bash unitycli.sh test --editmode

# Run only PlayMode tests
bash unitycli.sh test --playmode

# Run tests matching a specific name filter (regex or substring)
bash unitycli.sh test --filter "MyNamespace.MyTestClass"

# Run tests matching a specific category filter
bash unitycli.sh test --category "Smoke"
```

### Exit Codes
- `0`: Success (all tests passed, compilation succeeded, method executed successfully, or connection succeeded).
- `1`: Failure (compilation errors, failed tests, method execution exception, or connection check failed).

---

## Method Execution with Parameters

The `executemethod` subcommand executes static methods in the Unity editor AppDomain directly from the shell:

```bash
bash unitycli.sh executemethod Namespace.Class.Method 4 3.5 "hello" "{\"Value\":42}"
```

### Supported Parameter Types
- **Primitives**: `int`, `float`, `double`, `bool`, `long`, `decimal` (parsed using invariant culture).
- **Strings**: Standard C# strings.
- **Complex Types (JSON)**: Any other C# class/struct type will be automatically deserialized from its raw string parameter using Unity's `JsonUtility.FromJson`.

### Overload Resolution
Overloaded static methods are resolved automatically by matching the number of arguments provided.

### Return Value Serialization
- **Primitives & Strings**: Printed directly to the console.
- **Complex Types**: Automatically serialized and printed as a JSON payload using `JsonUtility.ToJson`.
- **Void/Null**: Prints `Unity Response: SUCCESS` (via socket) or no extra payload.

---

## Integration Tests

The repository includes a robust automated integration test suite `test.sh` to verify the CLI runner's correctness

### Running the tests
Simply execute:
```bash
bash test.sh
```

### Test Suite Execution Flow:
1. Detects if Unity is running for the project. If not, it launches Unity in the background and waits for the TCP server to start.
2. Runs a suite of test scenarios (such as compiling errors/warnings, skipped tests, executing successful/failing/missing methods) in **Online Mode** via TCP sockets.
3. Compares the normalized console output against verified outputs located under [IntegrationTests](file:///c:/Users/perev/Code/UnityCliRunner/IntegrationTests)/<TestCase>/output.online.verified.txt.
4. Gracefully terminates the running Unity Editor process.
5. Re-runs scenarios in **Auto-Start Mode** (starting with Unity stopped) to verify that commands automatically trigger the background Unity startup sequence and execute correctly.
6. Compares the console output against verified outputs under [IntegrationTests](file:///c:/Users/perev/Code/UnityCliRunner/IntegrationTests)/<TestCase>/output.autostart.verified.txt.
7. Restores any modified test files to their original state upon completion.

### Bootstrapping Verified Outputs
If you modify the output format of `unitycli.sh` and need to update the expected baselines, run the integration tests with the `BOOTSTRAP=true` environment variable:
```bash
BOOTSTRAP=true bash test.sh
```
This automatically overwrites all `output.online.verified.txt` and `output.autostart.verified.txt` files with the actual output generated during the test run.
