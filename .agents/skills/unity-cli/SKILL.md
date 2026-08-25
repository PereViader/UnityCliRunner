---
name: unity-cli
description: Use it to run Unity EditMode or PlayMode tests, trigger a Unity AssetDatabase refresh and recompilation, force a full C# recompilation, inspect Unity compiler warnings or errors printed to the terminal, debug failed Unity tests, evaluate dynamic C# expressions/scripts live in-memory, keep a background Unity instance warm for faster repeated runs, check/stop/wait for that background instance, or execute a Unity static method with optional primitive or JSON object parameters and terminal-returned results.
---

# Unity CLI

## Overview

`unitycli.sh` allows interacting with Unity3d to refresh the AssetDatabase before test/method work, surface compilation diagnostics in the terminal, print failed test details.

On windows, run it using the git bash.

Run commands with the current working directory set to the root of the unity project so it can find `ProjectSettings`, `Temp`, `UnityLockFile` and other Unity specific files.

When running in a sandboxed environment, the CLI needs local network permission to connect to the UnityCliRunner TCP socket on 127.0.0.1. Request or grant that network permission before running.

Command executions should not be timed out. Agents should wait for the command to finish or for the user to interrupt it manually.

## Refresh Workflow

Use `refresh` whenever the task needs Unity to import pending asset/script changes, wait for compilation to finish, and print compiler diagnostics without running tests or a custom method.

Use `refresh` after Unity C# or asset changes when compilation status matters but tests are unnecessary. Use `test` after test changes when failed-test details are needed. 


```bash
bash ./unitycli.sh refresh
```

When Unity is already running for this project, the wrapper connects to the UnityCliRunner socket, clears the active editor console, triggers `AssetDatabase.Refresh()`, waits for refresh/compilation/domain reloads to settle, then prints compiler warnings and errors captured from the Unity console.

When Unity is not running, the wrapper automatically starts a background Unity instance in batchmode first, and then executes the refresh command over TCP.

Treat `refresh` as a compile probe:

- Compiler warnings are printed and the command succeeds.
- Compiler errors are printed and the command exits non-zero.
- If Unity fails before compiler diagnostics are available, the wrapper prints the tail of the Unity refresh log.

## Recompile Workflow

Use `recompile` when you want to force a full C# recompilation (clearing the build cache) to reliably output and fetch all compiler warnings and errors from a clean state.

```bash
bash ./unitycli.sh recompile
```

This clears the compiler cache and forces Unity to rebuild all script assemblies from scratch.

## Test Workflow

Use `test` to compile the Unity project, run the test suite, and retrieve the results. If compilation fails, the compilation errors will be displayed instead.

```bash
bash ./unitycli.sh test
bash ./unitycli.sh test --editmode
bash ./unitycli.sh test --playmode
bash ./unitycli.sh test --editmode --filter SomeTestName
bash ./unitycli.sh test --playmode --category Smoke
```

Provide the relevant `--editmode` / `--playmode` flag when targeting some specific tests.
When none of the mode flags are supplied, the wrapper runs both modes.

When Unity is already running for this project, the wrapper connects to the UnityCliRunner socket, triggers an AssetDatabase refresh, waits for refresh/compilation to finish, then runs tests in the running editor. Connection failures during domain reload are expected; the wrapper polls until Unity is ready.

When Unity is not running, the wrapper automatically starts a background Unity instance in batchmode first, and then executes the tests.

Treat the terminal output as the primary debugging surface:

- Compilation warnings/errors are printed in build-style `file(line,column): warning/error ...` format.
- Test failures are printed after failed runs, including error messages and stack traces for each failed leaf test.
- Filtered runs that match zero tests are treated as failures with `No tests matched the supplied filter.`
- A non-zero exit means compilation, test execution, method execution, or Unity startup failed.
- If compilation fails, fix the compiler diagnostics before rerunning tests.

## Background Unity

Use the start/stop/status commands when repeated agent operations would be faster with Unity kept open, especially in worktrees or when no editor is already running.

```bash
bash ./unitycli.sh start batchmode
bash ./unitycli.sh start interactive
bash ./unitycli.sh status
bash ./unitycli.sh stop
```

`start batchmode` launches a headless-ish background Unity instance and waits until the socket runner is reachable. If the background instance is already starting or running, calling `start` will block and wait for it to be ready. `start interactive` opens a normal Unity editor instance but still enables the same socket workflow.

Use `status` before long validation loops. It reports:

- `Status: Not Running` when there is no project Unity lock.
- `Status: Ready` when the socket runner responds.
- `Status: Running Unreachable` when Unity is open but the socket runner cannot be reached, usually during startup, refresh, domain reload, or a broken editor state.

Use `stop` when the background instance is no longer needed; it asks the socket to exit and falls back to killing the project Unity process if needed.

## Execute Static Methods

Use `executemethod` to run a static method available in the Unity editor AppDomain. The method name must be `FullyQualifiedType.Method`; public and non-public static methods can be found.

Use `executemethod` before debugging Unity-only APIs that do not compile under plain `dotnet`, and when custom editor methods provide a better inspection or generation surface than ad hoc file parsing.

```bash
bash ./unitycli.sh executemethod Namespace.Class.Method
bash ./unitycli.sh executemethod Namespace.Class.Method 4 3
bash ./unitycli.sh executemethod Namespace.Class.Method '{"Value":4}'
```

Arguments are passed after the method name. Supported primitive conversions are `string`, `int`, `float`, `double`, `bool`, `long`, and `decimal`. Other parameter types are deserialized with Unity `JsonUtility.FromJson`, so object parameters must use JsonUtility-compatible JSON and serializable field shapes. Quote JSON carefully so the shell preserves it.

The runner resolves overloads by method name and argument count. If multiple static overloads have the same parameter count, it reports an ambiguous match; use uniquely named wrapper methods when needed.

Methods can return values. Successful primitive, string, decimal, bool, and `null` results are printed directly; object results are serialized with `JsonUtility.ToJson`; empty `void` successes print `Unity Response: SUCCESS`. Failures print the failure payload and return non-zero.

Like tests, `executemethod` reuses a running Unity socket when possible and otherwise automatically starts the background instance if it is not running. It also performs the AssetDatabase refresh/compilation readiness flow before invoking the method.

## Dynamic C# Evaluation (eval)

Use `eval` to run arbitrary C# expressions or code snippets live in the Unity Editor AppDomain without creating files or triggering domain reloads.

```bash
bash ./unitycli.sh eval "Application.unityVersion"
bash ./unitycli.sh eval "SceneManager.GetActiveScene().name"
bash ./unitycli.sh eval "var x = 10; var y = 20; return x + y;"
bash ./unitycli.sh eval 'new GameObject("MyGameObject")'
```

- **Instant Evaluation**: In-memory compilation via Roslyn compiler assemblies loaded dynamically by UnityCliRunner.
- **Smart Wrapping**: Single expressions (e.g. `1 + 1`), blocks with explicit `return`, and void statements (e.g. `Debug.Log("hi");`) are handled automatically.
- **Formatting**: Primitives, Strings, Booleans, GameObjects, Components, and Collections are automatically formatted and printed to stdout.
- **Diagnostics & Errors**: Syntax or compilation errors are formatted in standard compiler error format (`eval(line, col): error CSxxxx: ...`) with exit code `1`.
- **Runtime Exceptions**: Unhandled exceptions print the full exception message and stack trace with exit code `1`.
- **Fast Execution**: `eval` runs directly on the active Editor state without waiting for AssetDatabase refresh or triggering domain reload.