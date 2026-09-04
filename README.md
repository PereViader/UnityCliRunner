[![Test and publish](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityCliRunner?include_prereleases)](https://github.com/PereViader/UnityCliRunner/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityclirunner?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityclirunner/)

# UnityCliRunner

A native **Model Context Protocol (MCP)** server that connects AI coding agents (Antigravity, Claude Code, Cursor, VS Code) directly with the Unity Editor.

By communicating with a running Unity Editor (or a headless background instance) via loopback TCP sockets and exposing standard JSON-RPC stdio MCP tools, UnityCliRunner enables sub-second compilation feedback, instant test execution, dynamic C# evaluation, and static method invocations without shell quoting issues or slow batchmode restarts.

---

## Overview & Key Capabilities

UnityCliRunner provides 6 core MCP tools:

1. **`unity_status`**: Inspects Editor connection state (`Ready`, `Not Running`, `Compiling`, or `Running Unreachable`).
2. **`unity_refresh`**: Triggers `AssetDatabase.Refresh()` and returns compiler diagnostics.
3. **`unity_recompile`**: Cleans compiler cache and forces a full rebuild of script assemblies.
4. **`unity_run_tests`**: Runs EditMode and/or PlayMode unit and integration tests with granular filtering by name (`filter`) and category (`category`).
5. **`unity_execute_method`**: Executes static C# methods (`Namespace.Class.Method`) with typed arguments and returns formatted outputs and console logs.
6. **`unity_eval`**: Evaluates live C# expressions, statements, or multiline blocks dynamically in-memory without domain reloads.
7. **`unity_stop`**: Safely stops the running Unity background instance.

---

## MCP Tools at a Glance

| Tool | Parameters | Description |
| :--- | :--- | :--- |
| **`unity_status`** | _none_ | Checks Editor state (`Ready`, `Not Running`, etc.). |
| **`unity_refresh`** | _none_ | Triggers `AssetDatabase.Refresh()` and returns compilation diagnostics. |
| **`unity_recompile`** | _none_ | Forces a full C# recompilation (clears build cache) and returns compiler diagnostics. |
| **`unity_run_tests`** | `filter`, `category`, `mode` (`all`, `editmode`, `playmode`) | Runs tests and reports pass/fail/skip counts and failed stack traces. |
| **`unity_execute_method`** | `methodName`, `args` (array) | Executes static C# method with arguments (refreshes first, stops Play Mode). |
| **`unity_eval`** | `code` (string) | Evaluates C# expression or script dynamically in-memory against active Editor/Play Mode. |
| **`unity_stop`** | _none_ | Safely terminates the background Unity Editor instance. |

> **Auto-Start**: If Unity is not running when an operation is requested, UnityCliRunner automatically starts a headless background instance in batchmode first.

---

## Installation & Setup

### 1. Requirements
- **.NET 8.0 Runtime or SDK** (`dotnet`).
- **Unity**: Version 2021.3 or higher.

### 2. Install the Package

[Install from OpenUPM](https://openupm.com/packages/com.pereviader.unityclirunner/#modal-manualinstallation):
```bash
openupm add com.pereviader.unityclirunner
```
Or add via git URL in Unity's Package Manager:
```
https://github.com/PereViader/UnityCliRunner.git?path=src/UnityCliRunner.Unity3d/Packages/com.pereviader.unityclirunner
```

### 3. Install MCP Configurations

In the Unity Editor menu, select:
**Tools > UnityCliRunner > Install MCP Configurations**

This automatically creates or updates the configuration files for:
- **Antigravity**: `.agents/plugins/unity-cli/mcp_config.json`
- **VS Code**: `.vscode/mcp.json`
- **Cursor**: `.cursor/mcp.json`
- **Claude Code**: `.claude/mcp.json`
- **Codex**: `.codex/config.toml`

### Manual MCP Server Configuration

If configuring manually, add the following to your MCP client configuration:

```json
{
  "mcpServers": {
    "unity-cli": {
      "command": "dotnet",
      "args": [
        "UnityCliRunner.Mcp.dll"
      ],
      "cwd": "<path-to-project>/Packages/com.pereviader.unityclirunner/MCP~/"
    }
  }
}
```

---

## CLI & Direct Invocation

The MCP binary can also be called directly from the command line for testing or scripting:

```bash
# Check status
dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll call unity_status

# Evaluate C#
dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll call unity_eval '{"code":"1 + 1"}'

# Refresh AssetDatabase
dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll call unity_refresh

# Run tests
dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll call unity_run_tests '{"mode":"editmode"}'

# Execute static method
dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll call unity_execute_method '{"methodName":"Tests.DummyExecuteClass.SuccessMethod"}'
```

---

## License

MIT License. See [LICENSE.md](LICENSE.md) for details.
