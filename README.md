# UnityCliRunner

A lightweight, robust tool to compile Unity code, run tests, and execute custom editor methods directly from the command line. It enables extremely fast developer feedback loops and simplifies automation/AI agent workflows.

---

## What is this project about?

`UnityCliRunner` bridges the gap between external terminals and the Unity Editor. It comprises a C# TCP server package that runs in the Unity Editor and a companion bash script (`unitycli.sh`) to communicate with it.

It operates in two distinct modes depending on whether the editor is open:

- **Online Mode (Unity is Open)**: Communicates via a lightweight TCP loopback server to trigger `AssetDatabase.Refresh()`, wait for compilation, run tests, or execute static methods. Results and compiler output are streamed in real-time, reducing compilation and test feedback loops from minutes to sub-seconds.
- **Offline Mode (Unity is Closed)**: Automatically falls back to launching Unity in `-batchmode`, parsing Unity logs to extract clean compilation errors, warnings, and test results.

---

## Key Features

- ⚡ **Sub-Second Compilation Loop**: Keep Unity open, modify C# files, and run tests or methods instantly via TCP sockets without restarting the editor.
- 🤖 **Perfect for AI & CI/CD Workflows**: Headless tools can trigger compilations and check status without interacting with the graphical interface.
- 🎨 **Beautiful Compiler Output**: Extracts C# warnings and errors from Unity compilation log/tracker and prints them in a clean, `dotnet build` format with ANSI colors (warnings in yellow, errors in red).
- 🧪 **Flexible Test Runner**: Run EditMode or PlayMode tests (or both) and filter specific tests by name. Failed tests are printed in a clean `dotnet test` format.
- ⚙️ **Execute Custom Methods**: Run arbitrary static parameterless methods returning void (e.g., build scripts, setup methods) in both online and offline modes.
- 🔌 **Dynamic Port Assignment**: Avoids port conflicts by dynamically binding to a free loopback port on startup and writing it to `Temp/unity_cli_port.txt`.
- 🪟 **PowerShell Socket Bridge**: Uses PowerShell socket communication internally on Windows to avoid the subshell socket inheritance issues common in Git Bash.
- 📦 **UPM Package Support**: Clean package-based setup that doesn't clutter your project's main codebase.
- 🚦 **Robust Integration Tests**: Comes with a self-contained integration test suite to verify commands against various dummy states (compilation success/warnings/errors, test success/skipped/failures, method success/failures/not found).

---

## Installation & Setup

### 1. Install the Unity Package

Choose one of the standard Unity Package Manager (UPM) options:

#### Option A: Install via git URL (Recommended)
1. Open your Unity project's `Packages/manifest.json`.
2. Add the following entry to the `dependencies` block:
   ```json
   "com.pereviader.unityclirunner": "https://github.com/PereViader/UnityCliRunner.git?path=Packages/com.pereviader.unityclirunner"
   ```
3. Alternatively, in the Unity Editor, go to **Window > Package Manager**, click the **+** button in the top-left corner, select **Add package from git URL...**, and paste:
   `https://github.com/PereViader/UnityCliRunner.git?path=Packages/com.pereviader.unityclirunner`

#### Option B: Manual Installation (Legacy)
Copy the `Packages/com.pereviader.unityclirunner` folder from this repository into your Unity project's `Assets` directory (e.g., `Assets/UnityCliRunner`).

> [!IMPORTANT]
> Because it contains editor-only scripts, ensure the folder structure containing `UnityCliServer.cs` and `UnityCliCompilationTracker.cs` is kept under an `Editor` folder (or referenced by an Editor-only assembly definition).

---

### 2. Add the runner script

Copy `unitycli.sh` from the root of this repository to the root directory of your Unity project.

---

### 3. Requirements

- A shell environment capable of running Bash (e.g., Git Bash on Windows, macOS/Linux terminal).
- PowerShell (on Windows, used internally to establish TCP socket connections).
- Unity version 2021.3 or higher.

---

## How to use it

Run `unitycli.sh` from the root directory of your Unity project:

```bash
# Run both EditMode and PlayMode tests
./unitycli.sh test

# Run only EditMode tests
./unitycli.sh test --editmode

# Run only PlayMode tests
./unitycli.sh test --playmode

# Run tests matching a specific name filter (regex/substring)
./unitycli.sh test --filter "MyNamespace.MyTestClass"

# Execute a custom static parameterless method returning void
./unitycli.sh executemethod Namespace.Class.Method

# Check if Unity is running and block/wait until the connection is fully ready
./unitycli.sh check-connection

# Show help output
./unitycli.sh --help
```

### Exit Codes
- `0`: Success (all tests passed, compilation succeeded, method executed successfully, or connection succeeded).
- `1`: Failure (compilation errors, failed tests, method execution exception, or connection check failed).

---

## How it Works under the Hood

When the Unity Editor loads, `UnityCliServer` starts a background thread running a TCP listener bound to loopback `127.0.0.1` and a random free port assigned by the OS. The active port is saved to `Temp/unity_cli_port.txt`.

### Protocol Commands

External tools communicate using a line-based text protocol:
- `PING`: Responds `PONG`. Used to verify connection health.
- `REFRESH`: Triggers `AssetDatabase.Refresh()` on the Unity main thread.
- `POLL_REFRESH`: Returns compilation state (`COMPILING`, `UPDATING`, `COMPILATION_ERROR`, or `READY`).
- `RUN_TESTS <mode> [filter]`: Triggers EditMode or PlayMode tests.
- `POLL_TESTS`: Returns test running state (`RUNNING`, `SUCCESS <details>`, `FAILURE <details>`, `IDLE`, or `ERROR`).
- `EXECUTE_METHOD <method>`: Enqueues execution of a static parameterless void method.
- `POLL_EXECUTE`: Returns method running state (`RUNNING`, `SUCCESS`, `FAILURE <details>`, `IDLE`, or `ERROR`).

If Unity is closed, `unitycli.sh` parses `Temp/UnityLockfile` (or `UnityLockFile`), detects the absence of the running instance, and automatically runs the subcommand via Unity's batchmode:
- For tests: `-batchmode -runTests -testPlatform <EditMode|PlayMode>`
- For methods: `-batchmode -executeMethod <method>`

---

## Integration Tests

The repository includes a robust automated integration test suite (`unitycli_integration_tests.sh`) to verify the CLI runner's correctness in both online and offline environments.

### Running the tests
Simply execute:
```bash
./unitycli_integration_tests.sh
```

### What the test suite does:
1. Detects if Unity is running for the project. If not, it launches Unity in the background and waits for the TCP server to start.
2. Runs a suite of test scenarios (such as compiling errors/warnings, skipped tests, executing successful/failing/missing methods) in **Online Mode** via TCP sockets.
3. Compares the normalized console output against verified outputs located under `IntegrationTests/<TestCase>/output.online.verified.txt`.
4. Gracefully terminates the running Unity Editor process.
5. Re-runs the entire suite of scenarios in **Offline Mode** (batchmode).
6. Compares batchmode console output against verified outputs under `IntegrationTests/<TestCase>/output.offline.verified.txt`.
7. Restores any modified test files to their original state upon completion.

### Bootstrapping Verified Outputs
If you modify the output format of `unitycli.sh` and need to update the expected baselines, you can run the integration tests with the `BOOTSTRAP=true` environment variable:
```bash
BOOTSTRAP=true ./unitycli_integration_tests.sh
```
This automatically overwrites all `output.online.verified.txt` and `output.offline.verified.txt` files with the actual output generated during the test run.
