[![Test and publish](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml/badge.svg)](https://github.com/PereViader/UnityCliRunner/actions/workflows/TestAndPublish.yml) ![Unity version 2021.3](https://img.shields.io/badge/Unity-2021.3-57b9d3.svg?style=flat&logo=unity) [![GitHub Release](https://img.shields.io/github/v/release/PereViader/UnityCliRunner?include_prereleases)](https://github.com/PereViader/UnityCliRunner/releases) [![openupm](https://img.shields.io/npm/v/com.pereviader.unityclirunner?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.pereviader.unityclirunner/)

# UnityCliRunner

A native **Model Context Protocol (MCP)** server that connects AI coding agents (Antigravity, Claude Code, Cursor, VS Code) directly with the Unity Editor.

By communicating with a running Unity Editor (or a headless background instance) via loopback TCP sockets and exposing standard JSON-RPC stdio MCP tools, UnityCliRunner enables sub-second compilation feedback, instant test execution, dynamic C# evaluation, and static method invocations without shell quoting issues or slow batchmode restarts.

---

## Overview & Key Capabilities

UnityCliRunner provides 7 MCP tools:

1. **`unity_status`**: Inspects Editor connection state (`Ready`, `Not Running`, `Compiling`, or `Running Unreachable`). Unity automatically starts on demand when action tools are invoked.
2. **`unity_refresh`**: Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Action tools auto-refresh before executing.
3. **`unity_recompile`**: Forces clean script rebuild by clearing compiler cache. Slower than unity_refresh; use only for stale/corrupted assembly cache.
4. **`unity_run_tests`**: Runs EditMode/PlayMode tests with failure diagnostics. Auto-refreshes pending script changes first; do not call unity_refresh beforehand.
5. **`unity_execute_method`**: Executes static C# methods (`Namespace.Class.Method`) with typed arguments and returns formatted outputs and console logs.
6. **`unity_eval`**: Evaluates C# snippet in-memory (<80ms) without domain reload. Primary tool to query scene, GameObjects, and component state.
7. **`unity_stop`**: Safely terminates the background Unity Editor instance (to release project locks or recover from hangs).

---

## MCP Tools at a Glance

| Tool | Parameters | Description |
| :--- | :--- | :--- |
| **`unity_status`** | _none_ | Checks Editor state (`Ready`, `Not Running`, etc.). Auto-starts on demand for action tools. |
| **`unity_refresh`** | _none_ | Refreshes AssetDatabase and returns compiler diagnostics. Fast (<200ms) when unchanged. Action tools auto-refresh before executing. |
| **`unity_recompile`** | _none_ | Forces clean script rebuild by clearing compiler cache. Slower than unity_refresh; use only for stale/corrupted assembly cache. |
| **`unity_run_tests`** | `filter`, `category`, `mode` (`all`, `editmode`, `playmode`), `failedOnly` | Runs EditMode/PlayMode tests with failure diagnostics. Auto-refreshes pending script changes first; do not call unity_refresh beforehand. |
| **`unity_execute_method`** | `methodName`, `args` (array) | Executes static C# method with arguments (refreshes first, stops Play Mode). |
| **`unity_eval`** | `code` (string) | Evaluates C# snippet in-memory (<80ms) without domain reload. Primary tool to query scene, GameObjects, and component state. |
| **`unity_stop`** | _none_ | Safely terminates the background instance (used to release GUI locks or recover; do not stop routinely). |

> **Auto-Start & Warm Instance**: If Unity is not running when an operation is requested, UnityCliRunner automatically starts a headless background instance in batchmode first and keeps it warm for subsequent commands.

---

## Installation & Setup

### 1. Requirements
- **.NET 10.0 Runtime or SDK** (`dotnet`).
- **Unity**: Version 2021.3 or higher.

### 2. Install the Package

[Install from OpenUPM](https://openupm.com/packages/com.pereviader.unityclirunner/#modal-manualinstallation):
```bash
openupm add com.pereviader.unityclirunner
```

### 3. Install MCP Configurations

In the Unity Editor menu, select:
**Tools > UnityCliRunner > Install MCP Configurations**

This automatically creates or updates the configuration files for:
- **Antigravity**: `.agents/plugins/unity-cli/mcp_config.json`
- **VS Code**: `.vscode/mcp.json`
- **Cursor**: `.cursor/mcp.json`
- **Claude Code**: `.mcp.json`
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

## Testing & MCP Inspection

The MCP server communicates over standard input/output using JSON-RPC. To inspect, test, and interact with the tools interactively, you can use the official MCP Inspector:

```bash
npx @modelcontextprotocol/inspector dotnet Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll --project <path-to-unity-project>
```

---

## License

MIT License. See [LICENSE.md](LICENSE.md) for details.
