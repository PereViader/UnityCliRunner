---
name: unity-cli
description: Use it to run Unity EditMode or PlayMode tests, trigger a Unity AssetDatabase refresh and recompilation, inspect Unity compiler warnings or errors printed to the terminal, debug failed Unity tests, keep a background Unity instance warm for faster repeated runs, check/stop/wait for that background instance, or execute a Unity static method with optional primitive or JSON object parameters and terminal-returned results.
---

# Unity CLI

## Overview

`unitycli.sh` allows interacting with Unity3d to refresh the AssetDatabase before test/method work, surface compilation diagnostics in the terminal, print failed test details.

On windows, run it using the git bash or sh.

Run commands with the current working directory set to the root of the unity project so it can find `ProjectSettings`, `Temp`, `UnityLockFile` and other Unity specific files.

## Refresh Workflow

Use `refresh` whenever the task needs Unity to import pending asset/script changes, wait for compilation to finish, and print compiler diagnostics without running tests or a custom method.

Use `refresh` after Unity C# or asset changes when compilation status matters but tests are unnecessary. Use `test` after test changes when failed-test details are needed. 


```bash
bash ./unitycli.sh refresh
```

When Unity is already running for this project, the wrapper connects to the UnityCliRunner socket, clears the active editor console, triggers `AssetDatabase.Refresh()`, waits for refresh/compilation/domain reloads to settle, then prints compiler warnings and errors captured from the Unity console.

When Unity is not running, the wrapper opens the project in batchmode, lets Unity refresh/import/compile, parses the Unity log for compiler warnings and errors, and quits.

Treat `refresh` as a compile probe:

- Compiler warnings are printed and the command succeeds.
- Compiler errors are printed and the command exits non-zero.
- If Unity fails before compiler diagnostics are available, the wrapper prints the tail of the Unity refresh log.

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

When Unity is not running, the wrapper finds the configured Unity editor, opens the project in batchmode, runs the requested tests, writes results under `Temp`, and quits.

Treat the terminal output as the primary debugging surface:

- Compilation warnings/errors are printed in build-style `file(line,column): warning/error ...` format.
- Test failures are printed after failed runs, including error messages and stack traces for each failed leaf test.
- Filtered runs that match zero tests are treated as failures with `No tests matched the supplied filter.`
- A non-zero exit means compilation, test execution, method execution, or Unity startup failed.
- If compilation fails, fix the compiler diagnostics before rerunning tests.

## Background Unity

Use the start/stop/status/wait-ready commands when repeated agent operations would be faster with Unity kept open, especially in worktrees or when no editor is already running.

```bash
bash ./unitycli.sh start batchmode
bash ./unitycli.sh start interactive
bash ./unitycli.sh status
bash ./unitycli.sh wait-ready
bash ./unitycli.sh stop
```

`start batchmode` launches a headless-ish background Unity instance and waits until the socket runner is reachable and refresh is ready or compilation has produced errors. `start interactive` opens a normal Unity editor instance but still enables the same socket workflow.

Use `status` before long validation loops. It reports:

- `Status: Not Running` when there is no project Unity lock.
- `Status: Ready` when the socket runner responds.
- `Status: Running Unreachable` when Unity is open but the socket runner cannot be reached, usually during startup, refresh, domain reload, or a broken editor state.

Use `wait-ready` when Unity is already running and the workflow should pause until the socket is reachable. Use `stop` when the background instance is no longer needed; it asks the socket to exit and falls back to killing the project Unity process if needed.

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

Like tests, `executemethod` reuses a running Unity socket when possible and otherwise falls back to batchmode execution. It also performs the AssetDatabase refresh/compilation readiness flow before invoking the method when Unity is already running.