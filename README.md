[![Test and publish](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityCliRunner?include_prereleases)](https://github.com/PereViader/UnityCliRunner/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityclirunner?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityclirunner/)

# UnityCliRunner

A lightweight, high-performance command-line runner that bridges your shell and AI coding agents with the Unity Editor.

By communicating with a running Unity Editor (or a headless background instance) via loopback TCP sockets, UnityCliRunner enables sub-second compilation feedback, instant test execution, dynamic C# evaluation, and static method invocations without slow batchmode restarts.

---

## Overview & Key Capabilities

UnityCliRunner provides 5 core pillars of functionality:

1. **Compilation & Diagnostics (`refresh`, `recompile`)**: Trigger an Asset Database refresh or force a full clean C# rebuild, streaming compiler errors and warnings directly back to your terminal formatted in standard compiler diagnostics.
2. **Test Execution (`test`)**: Run EditMode and PlayMode tests with granular name (`--filter`) and category (`--category`) filtering, outputting clear test summaries and failed-test stack traces.
3. **Project Commands (`executemethod`)**: Execute reusable static C# methods with typed arguments (primitives, strings, or JSON-deserialized objects) and structured return values.
4. **Dynamic C# Evaluation (`eval`)**: Dynamically compile and evaluate expressions or multi-statement snippets in-memory against the live Unity Editor or Play Mode state without domain reload.
5. **Background Process Management (`start`, `stop`, `status`, `wait-ready`)**: Keep a warm headless or interactive Unity Editor running in the background for instant repeated executions.

---

## CLI Commands at a Glance

Run `bash unitycli.sh <command> [options]` from your Unity project root:

| Command | Syntax | Description |
| :--- | :--- | :--- |
| **`refresh`** | `bash unitycli.sh refresh` | Triggers `AssetDatabase.Refresh()` and prints compilation errors and warnings. |
| **`recompile`** | `bash unitycli.sh recompile` | Forces a full C# recompilation (clears build cache) and prints compiler diagnostics. |
| **`test`** | `bash unitycli.sh test [options]` | Runs EditMode and/or PlayMode unit and integration tests (both by default). |
| **`executemethod`** | `bash unitycli.sh executemethod <Method> [args...]` | Executes a C# static method with optional typed parameters (refreshes first, stops Play Mode). |
| **`eval`** | `bash unitycli.sh eval "<code>"` | Evaluates a live C# expression or script dynamically in-memory (preserves Play Mode). |
| **`start`** | `bash unitycli.sh start <mode>` | Starts a background Unity instance (`batchmode` or `interactive`) and waits until ready. |
| **`stop`** | `bash unitycli.sh stop` | Safely stops the background Unity instance. |
| **`status`** | `bash unitycli.sh status` | Checks the status of the background Unity instance (`Ready`, `Not Running`, etc.). |
| **`wait-ready`** | `bash unitycli.sh wait-ready` | Blocks and waits until a running Unity instance is reachable and ready to receive commands. |

> **Note on Auto-Start**: If Unity is not already running when you run `refresh`, `recompile`, `test`, `executemethod`, or `eval`, UnityCliRunner will automatically launch a background instance in batchmode first before executing the command.

---

## Installation & Setup

### 1. Requirements
- **macOS / Linux**: Terminal with Bash.
- **Windows**: Git Bash (included with Git for Windows).
- **Unity**: Version 2021.3 or higher.

### 2. Install the Package

[Install from OpenUPM](https://openupm.com/packages/com.pereviader.unityclirunner/#modal-manualinstallation):
```bash
openupm add com.pereviader.unityclirunner
```
Or add the git URL / package via Unity's Package Manager.

### 3. Install unitycli.sh and Agent Skill

In the Unity Editor, run the installers from the top menu dropdown:
- **Tools > UnityCliRunner > InstallBashScript**: Copies the runner script (`unitycli.sh`) to the root of your Unity project.
- **Tools > UnityCliRunner > InstallSkill**: Copies the `.agents/skills/unity-cli` skill folder to the root of your project (for agentic AI tools like **Antigravity**, **Claude**, **Codex**, **Gemini**).

*Note: If your Unity project is in a subdirectory of your git repository, you can move the `.agents` folder to the root of the repository.*

---

## AI Agent Integration & Agent Skills

If you use agentic AI tools, UnityCliRunner includes a pre-packaged **Agent Skill** under `.agents/skills/unity-cli`.

### Benefits for AI Agents:
- **Sub-Second Feedback Loops**: Agents compile code and run tests in milliseconds instead of waiting ~30s for Unity batchmode restarts.
- **Persistent Background Editor**: Keeps a background headless Unity instance open across tool turns.
- **Standardized Compiler Diagnostics**: Compilation errors and test failures are formatted in standard `file(line,col): error CSxxxx: ...` syntax, enabling agents to parse, locate, and fix issues autonomously.

---

## Command Reference & Usage

### 1. Compilation & Diagnostics

#### `refresh`
Triggers `AssetDatabase.Refresh()`, waits for compilation and domain reloads to settle, and prints all compilation warnings and errors.
```bash
bash unitycli.sh refresh
```

#### `recompile`
Forces a clean recompilation by clearing the compiler cache and rebuilding all script assemblies from scratch.
```bash
bash unitycli.sh recompile
```

---

### 2. Running Tests

#### `test`
Runs Unity EditMode and/or PlayMode tests and prints failed-test stack traces:

```bash
# Run all tests (both EditMode and PlayMode)
bash unitycli.sh test

# Run only EditMode tests
bash unitycli.sh test --editmode

# Run only PlayMode tests
bash unitycli.sh test --playmode

# Filter tests by name (substring or regex match)
bash unitycli.sh test --editmode --filter "MyNamespace.MyTestClass"

# Filter tests by NUnit category (supports category negation like '!LongRunning')
bash unitycli.sh test --playmode --category "Smoke"
```

---

### 3. Executing Static Methods (`executemethod`)

Executes any static method (`FullyQualifiedType.Method`) available in the Unity Editor AppDomain:

```bash
bash unitycli.sh executemethod Namespace.Class.Method [args...]
```

#### Examples:
```bash
# Method with no arguments
bash unitycli.sh executemethod MyProject.Editor.BuildPipeline.BuildAddressables

# Method with primitive parameters (int, float, string)
bash unitycli.sh executemethod MyProject.Editor.AssetGenerator.CreateGrid 10 20 "forest"

# Method with JSON object parameter (deserialized via JsonUtility.FromJson)
bash unitycli.sh executemethod MyProject.Editor.ConfigLoader.ApplyConfig '{"difficulty":2,"enableCheats":false}'
```

#### Parameter & Return Value Handling:
- **Input Primitives**: `int`, `float`, `double`, `bool`, `long`, `decimal`, and `string`.
- **Complex Types**: Deserialized from JSON strings using Unity's `JsonUtility.FromJson`.
- **Overload Resolution**: Automatically resolved by matching the argument count.
- **Return Values**: Primitives and strings are printed directly; complex objects are serialized to JSON via `JsonUtility.ToJson`; `void` methods print `Unity Response: SUCCESS`.
- **Console Logs**: All console logs (`Debug.Log`, `Debug.LogWarning`, `Debug.LogError`, `Console.WriteLine`) emitted during execution are redirected to standard output.

---

### 4. Dynamic C# Evaluation (`eval`)

Dynamically compiles and executes arbitrary C# expressions or statements in-memory via Roslyn compiler assemblies loaded by UnityCliRunner:

```bash
# Evaluate a simple expression
bash unitycli.sh eval "1 + 1"
bash unitycli.sh eval "Application.unityVersion"
bash unitycli.sh eval "Mathf.Sqrt(64f)"

# Evaluate multi-statement blocks with return
bash unitycli.sh eval "var count = GameObject.FindObjectsOfType<Camera>().Length; return count;"

# Execute void statements or method calls (wrap in single quotes so C# double quotes are preserved)
bash unitycli.sh eval 'Debug.Log("Hello from CLI");'
bash unitycli.sh eval "System.GC.Collect()"

# Inspect GameObjects, Components, and Collections
bash unitycli.sh eval "new int[] { 10, 20, 30 }"
bash unitycli.sh eval 'GameObject.Find("Main Camera")'
```

> **Shell Quoting Note**: In C#, string literals require double quotes (`"..."`). When invoking from Bash, wrap the snippet in single quotes (`'...'`) so C# double quotes are preserved. When invoking from **PowerShell**, use triple quotes inside single quotes (e.g. `bash unitycli.sh eval 'Debug.Log("""Hello""");'`) or the stop-parsing token `bash --% unitycli.sh eval 'Debug.Log("Hello");'` to prevent PowerShell from stripping quotes.

#### Features:
- **In-Memory Compilation**: Executes immediately without domain reloads or disk file generation.
- **Console Logs**: Console messages (`Debug.Log`, `Debug.LogWarning`, `Debug.LogError`, `Console.WriteLine`) emitted during evaluation are captured and printed to standard output.
- **Smart Formatting**: Primitives, Booleans, Strings, GameObjects, Components, and Collections are automatically formatted for the terminal.
- **Standard Diagnostics**: Syntax errors and compile errors are reported with line/column coordinates in standard compiler error format (`eval(line, col): error CSxxxx: ...`).

---

### 5. Choosing Between `executemethod` and `eval`

Both commands execute Unity-side C# code, but they serve distinct purposes:

| Feature | `executemethod` | `eval` |
| :--- | :--- | :--- |
| **Primary Use Case** | Reusable project commands, automation, CI | One-off queries, debugging, live scene inspection |
| **Target Code** | Existing static method (`Namespace.Class.Method`) | Arbitrary C# expression or code snippet |
| **Asset Refresh** | Refreshes and recompiles before running | No refresh (runs immediately against current loaded state) |
| **Play Mode** | Stops Play Mode before execution | Preserves Play Mode (inspects live runtime state) |
| **Arguments** | Positional typed primitives / JSON objects | Inline C# code |
| **Output** | Stable machine-readable output / JSON | Richly formatted console diagnostics |

#### Example: Running a Build Utility
```bash
# Using executemethod for a repeatable, stable command:
bash unitycli.sh executemethod MyProject.Editor.BuildTools.GenerateAssets '{"outputPath":"Assets/Generated"}'
```

#### Example: Inspecting Active Scene
```bash
# Using eval to query live state:
bash unitycli.sh eval 'SceneManager.GetActiveScene().name'
```

---

### 6. Background Instance Management

Manage a background Unity Editor process to keep the TCP socket connection warm for instant sub-second commands:

```bash
# Start a background Unity instance in headless batchmode
# (If already starting or running, blocks and waits until it is ready)
bash unitycli.sh start batchmode

# Start a background Unity instance with the Editor GUI visible
bash unitycli.sh start interactive

# Check if the background Unity instance is running and reachable
bash unitycli.sh status

# Block and wait until a running Unity instance is reachable
bash unitycli.sh wait-ready

# Safely stop the background Unity instance (via socket EXIT, fallback to PID kill)
bash unitycli.sh stop
```

#### Status Outputs:
- `Status: Ready`: Unity is running and the TCP socket is responsive.
- `Status: Not Running`: No Unity instance is running for this project.
- `Status: Running Unreachable`: Unity is open but busy (e.g. compiling, domain reloading, or starting up).

---

## Exit Codes

- `0`: Success (compilation succeeded, all tests passed, method executed successfully, or connection succeeded).
- `1`: Failure (compilation errors, failed tests, runtime exception, invalid command/arguments, or connection failed).

---

## Integration Tests & Development

The repository includes a comprehensive automated integration test suite (`test.sh`) to verify socket communication, autostart behavior, test execution, method invocation, and eval across platforms.

### Running Integration Tests:
```bash
bash test.sh
```

### Filtering Specific Test Cases:
```bash
bash test.sh --filter TestEvalSuccess
bash test.sh --filter TestExecuteParams
```

### Updating Verified Baselines:
If output normalization or formatting changes, update the golden test baselines using `BOOTSTRAP=true`:
```bash
BOOTSTRAP=true bash test.sh
```
