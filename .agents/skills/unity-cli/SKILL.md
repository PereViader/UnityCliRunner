---
name: unity-cli
description: Model Context Protocol (MCP) server for Unity Editor - run unit tests, trigger compilation, evaluate live C#, invoke static methods, or manage Editor instances.
---

# Unity Model Context Protocol (MCP) Server

Interact directly with the running Unity Editor via typed Model Context Protocol (MCP) tools over local stdio JSON-RPC.

## Server Configuration

The MCP server binary is bundled directly in the package at `Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll`.

### MCP Server Config (`mcp.json` / `mcp_config.json`)

```json
{
  "mcpServers": {
    "unity-cli": {
      "command": "dotnet",
      "args": [
        "Packages/com.pereviader.unityclirunner/MCP~/UnityCliRunner.Mcp.dll"
      ]
    }
  }
}
```

To automatically generate configurations for Antigravity, VS Code, Cursor, and Claude Code:
Open the Unity Editor and select:
**Tools > UnityCliRunner > Install MCP Configurations**

---

## Available MCP Tools

### 1. `unity_status`
- **Description**: Returns the current Editor connection state (`Ready`, `Not Running`, `Compiling`, or `Running Unreachable`).
- **Parameters**: none.

### 2. `unity_refresh`
- **Description**: Triggers `AssetDatabase.Refresh()`, waits for compilation to finish, and returns compiler diagnostics.
- **Parameters**: none.

### 3. `unity_recompile`
- **Description**: Cleans build cache, forces a complete script recompilation, and returns compiler diagnostics.
- **Parameters**: none.

### 4. `unity_eval`
- **Description**: Evaluates dynamic C# code or expressions in-memory against the active Editor or running Play Mode state without domain reloads.
- **Parameters**:
  - `code` (string, required): C# expression (e.g. `1 + 1`, `Application.unityVersion`), void statement (e.g. `Debug.Log("Hi");`), or multi-statement block with explicit `return`.
- **Note**: Quoting does NOT require shell escaping. Strings inside `code` use standard C# double quotes (`"..."`).

### 5. `unity_execute_method`
- **Description**: Invokes static C# methods with typed parameters (`FullyQualifiedType.Method`). Refreshes assets and stops Play Mode prior to execution.
- **Parameters**:
  - `methodName` (string, required): Fully qualified method name (e.g. `Namespace.Class.Method`).
  - `args` (string[], optional): Array of arguments passed to the method. Primitives and JSON-deserialized objects are supported.

### 6. `unity_run_tests`
- **Description**: Executes EditMode and/or PlayMode unit and integration tests.
- **Parameters**:
  - `filter` (string, optional): Test name substring or regex.
  - `category` (string, optional): NUnit category (supports `!Category` negation).
  - `mode` (string, optional): `editmode` (default), `playmode`, or `all`.

### 7. `unity_stop`
- **Description**: Safely stops the running Unity background instance.
- **Parameters**: none.

---

## Behavior & Resilience

- **Auto-Start**: If Unity is not running, calling `unity_refresh`, `unity_recompile`, `unity_eval`, `unity_execute_method`, or `unity_run_tests` automatically launches a headless background Unity Editor in batchmode.
- **Domain Reload Tolerance**: Domain reloads triggered during operations are handled asynchronously by tracking operations in `Temp/` without crashing or dropping connections.
