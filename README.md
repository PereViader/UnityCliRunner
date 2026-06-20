# UnityCliRunner

A lightweight tool to compile Unity code and run tests from the command line, enabling fast iteration cycles for both local developers and automated/AI workflows.

## What is this project about?

`UnityCliRunner` consists of a C# TCP server script that runs inside the Unity Editor and a companion bash script (`unitycli.sh`) to communicate with it. It bridges the gap between external terminal tools and the Unity Editor:

- **When Unity is running**: It uses a lightweight TCP server to trigger `AssetDatabase.Refresh()` and execute EditMode or PlayMode tests. It then streams compilation errors/warnings and test results back to your terminal in real-time.
- **When Unity is closed**: It automatically falls back to running tests in Unity's `-batchmode`, parsing log files to output formatted test failures and compiler diagnostics.

---

## When is this useful?

This project is especially useful for **AI coding workflows** (utilizing tools/models like Gemini, Claude, Codex, etc.) or **CI/CD pipelines**:

- **Headless Iteration**: AI tools make changes to files but do not interact with the active Unity GUI or IDE interfaces directly. By calling `unitycli.sh`, these tools can trigger Unity's internal compilation and get immediate feedback.
- **Fast Feedback Loop**: Restarting Unity in batch mode to compile and test code takes a long time. With `UnityCliRunner` active in an open Unity Editor, compilation and test verification are triggered via sockets, reducing feedback time from minutes to seconds.
- **Structured Error Parsing**: It catches C# compilation errors and failed tests, formats them in a clear, `dotnet`-like structure, and exits with appropriate exit codes (`0` for success, `1` for failures), allowing scripts to act on them.

---

## How to install it

### Option 1: Install as a Package (Recommended)

You can add this project directly as a Unity Package using Unity Package Manager (UPM):

1. Open your Unity project's `Packages/manifest.json` file.
2. Add the following line to the `dependencies` block:
   ```json
   "com.pereviader.unityclirunner": "https://github.com/PereViader/UnityCliRunner.git?path=Packages/com.pereviader.unityclirunner"
   ```
3. Alternatively, in the Unity Editor, open **Window > Package Manager**, click the **+** icon in the top left, select **Add package from git URL...**, and paste:
   `https://github.com/PereViader/UnityCliRunner.git?path=Packages/com.pereviader.unityclirunner`

### Option 2: Manual Installation (Legacy)

1. Copy the `Packages/com.pereviader.unityclirunner` folder from this repository into your Unity project's `Assets` directory (e.g., `Assets/UnityCliRunner`).
   > **Note**: It contains C# scripts inside an `Editor` folder (`UnityCliServer.cs` and `UnityCliCompilationTracker.cs`) along with an assembly definition file (`UnityCliRunner.Editor.asmdef`). It will run automatically when the Unity Editor loads.

---

### Step 2: Add the test runner script
Copy the `unitycli.sh` script to the root directory of your Unity project.

### Step 3: Requirements
- A shell environment capable of running Bash (e.g., Git Bash on Windows, macOS/Linux terminal).
- PowerShell installed (used internally by `unitycli.sh` on Windows to handle socket communication cleanly without subshell socket limitations).

---

## How to use it

Run the script from the root of your project:

```bash
# Run both EditMode and PlayMode tests
./unitycli.sh test

# Run only EditMode tests
./unitycli.sh test --editmode

# Run only PlayMode tests
./unitycli.sh test --playmode

# Run tests matching a specific filter
./unitycli.sh test --filter "MyTestsCategory"

# Execute a custom static parameterless method returning void
./unitycli.sh executemethod Namespace.Class.Method

# Check if Unity is running and wait for connection to be ready
./unitycli.sh check-connection
```

### Key Features:
- **Auto-detection**: The script automatically checks if Unity is running for the project (using the lockfile).
- **Compilation Reporting**: If a compilation error occurs, the runner outputs the exact file, line number, and error details so you can fix it immediately.
- **Real-Time Polling**: It prints progress indicator dots while compiling or waiting for test/method completion.
