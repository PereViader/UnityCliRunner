# Findings

This document records observed Unity Editor and Test Framework behavior, together with reliable design rules for UnityCLI and similar tools that must operate across compilation, domain reloads, shutdowns, reconnects, and multiple desktop platforms.

## Domain reload and Editor lifecycle

- Unity's managed domain reload is asynchronous and can be triggered externally by script changes, compilation, package activity, Editor interaction, or shutdown. Every command must tolerate interruption and reinitialization at any point, including before its first callback or after its work has completed but before its response is delivered.
- Static fields, callbacks, background threads, sockets, and managed objects do not survive a domain reload. Persist the minimum operation identity and recovery state outside ordinary managed state before starting work that can reload the domain.
- `SessionState` survives a domain reload within the same Editor process but is reset when the process restarts. A persisted `SessionState` identifier can distinguish same-process reload recovery from an Editor restart.
- `AssemblyReloadEvents.beforeAssemblyReload` runs before the old domain is unloaded, but very little managed lifetime remains. Use it only for small, idempotent durable transitions and transport shutdown; never depend on a later old-domain callback.
- Register lifecycle callbacks again in every new domain. Registration and recovery must both be idempotent because initialization order and partially completed prior cleanup cannot be assumed.
- A reload and an Editor shutdown are interruption events, not ordinary command failures. Record an explicit interrupted outcome when possible, and do not silently convert either event into success.
- Socket-initiated Editor shutdown (`EXIT`) should stop listener services and invoke process exit (`EditorApplication.Exit(0)`) immediately rather than deferring exit across update ticks to wait for active operations. Unity's standard quitting lifecycle (`EditorApplication.quitting`) invokes interruption recovery handlers to mark any in-flight operations as interrupted in durable state cleanly and quickly.
- Unity may lock assembly reloads while tests are running. Script changes made during a test can be queued until the Test Framework releases that lock, so a test that edits a script does not necessarily exercise mid-test domain reload recovery.
- Stop Play Mode asynchronously and wait until both `EditorApplication.isPlaying` and `isPlayingOrWillChangePlaymode` are false before starting an operation that requires Edit Mode.

## Durable operations and interruption recovery

- Give every mutating request a unique client-generated operation ID. Include it in the start request, every poll, durable state, running marker, and terminal result.
- Serialize mutating operations through one project-scoped owner record. An identical operation ID is an idempotent retry; a different active ID must receive an explicit busy response instead of racing shared Unity or filesystem state.
- Make the operation lifecycle transactional: claim ownership, persist intent, begin Unity work, atomically persist the terminal result, then release ownership. Never clear ownership or a running marker before the result is durable.
- A command may finish inside Unity while its socket response is lost to reload. Retry only after a transport failure, and make the retry retrieve the existing correlated result rather than start the work again.
- Never delete or overwrite a marker/result unless its operation ID matches the caller. A stale callback, retry, or cleanup path must not mutate a newer operation's state.
- Recovery must use operation identities or generations. A callback from an older test run or domain must never overwrite state created by a newer request.
- Avoid deleting existing terminal result files (e.g., test/execute/eval result JSON) in-place at the beginning of an operation. Because clients match results by client-generated `operationId` and writers atomically replace results upon completion, in-place deletion is redundant and exposes transient 0-byte or `DELETE_PENDING` states to concurrent background readers on Windows NTFS.
- Polling handlers must evaluate operation state in strict hierarchical precedence: check the terminal result file for a matching client-generated operation ID first; if the result is absent or belongs to an older run, check the active running marker next; only if neither matches, query the durable operation journal for foreign ownership (yielding `BUSY`) or idle state. A poll handler must never return `IDLE` solely because an older terminal result file on disk failed to match the current operation ID, as this prematurely declares idle state while the newer operation is actively running.
- Reload and shutdown handling must be idempotent. Repeated lifecycle callbacks, duplicate Test Framework callbacks, or recovery after a partial cleanup must converge on one terminal outcome.
- A missing, unreadable, or malformed recovery record is not successful completion. Clear any in-memory snapshot, quarantine malformed data where practical, and report a recoverable error rather than guessing.
- In line-oriented socket protocols returning single-line status responses (e.g., `SUCCESS <payload>`), JSON serialization must be compact (`JsonUtility.ToJson(result, false)`). Emitting pretty-printed multi-line JSON in single-line responses causes socket line readers and shell scripts to capture only the opening brace (`{`), truncating the response. All command responses must adhere to strict single-line status framing (`SUCCESS <payload>` / `FAILURE <message>` / `INTERRUPTION <message>`) or dedicated terminal result file readers.
- Multi-line command inputs (such as dynamic `eval` C# code snippets or `executemethod` string parameters containing physical newlines) must be escaped across line-oriented socket protocols into single-line tokens (`\n`, `\r`, `\t`, `\\`, `\"`) and unescaped on the server before compilation or dispatch. Transmitting raw physical newlines breaks line-based stream framing, causing server readers (`ReadLine()`) to truncate the payload after the first line.
- Reflected static method lookup must enforce strict parameter arity matching. If no method overload matches the exact number of passed arguments, resolution must return `null` immediately rather than fall back to an arbitrary overload of the same name. Loose fallback matching causes nondeterministic method selection and converts missing-method errors into confusing runtime parameter-mismatch exceptions.
- CLI parameter parsing for arbitrary reflected static methods must support `Enum` (by name and integral value), `Nullable<T>`, `Guid`, `char`, and standard primitive/floating-point types with invariant culture (`CultureInfo.InvariantCulture`) before falling back to JSON deserialization. This allows command-line invocations to interact natively with standard Unity APIs and custom Editor methods without requiring bespoke string-wrapper methods.
- Persist only simple, version-tolerant data such as operation ID, kind, status, Editor-session ID, and timestamps. Reconstruct observers and Unity objects in the new domain instead of serializing runtime objects.
- Subcommand CLI frontends must perform strict, fail-fast argument validation (e.g., rejecting unexpected arguments on zero-parameter commands like `refresh`, `recompile`, `stop`, `status`, and validating required arguments for `eval`, `executemethod`, and test filters) before initiating socket connections or spawning background processes.

## Threading and transport

- Unity APIs are main-thread-affine unless explicitly documented otherwise. This includes APIs that can look like utility code, such as Unity serialization. Dispatch command handlers to the Editor thread by default.
- Unity does not guarantee the execution order of `[InitializeOnLoad]` classes across assemblies. If a background thread (e.g. the socket server thread) accesses a static class before the Unity main thread executes its static constructor, the CLR runs that static constructor on the background thread. Calling main-thread-affine Unity APIs (`EditorUtility`, `SessionState`, `EditorApplication`, `Application.dataPath`) from a static constructor will throw a `UnityException` and permanently poison the type with a `TypeInitializationException` for the lifetime of that domain. Helper and path classes must eliminate static constructors and static field initializers, defaulting to neutral values and relying on explicit main-thread `EnsureInitialized()` methods invoked sequentially during startup.
- Enforce explicit main-thread service initialization before starting background listener threads. The TCP socket server must only begin accepting external connections after dependent main-thread state (`CommandHelper`, `UnityCliPaths`, `UnityCliOperationStore`, `UnityCliCompilationTracker`, `UnityCliDispatcher`, `RoslynCompilerHelper`, test callbacks) has completed initialization.
- Structure command handler dispatching polymorphically by execution target (e.g., `WorkerThread` for lock-free snapshot polling, `MainThread` for serialized Unity API execution, `EditModeOnly` for operations requiring asynchronous Play Mode exit) rather than using hardcoded type-checking in the server loop.
- Keep socket acceptance and transport waiting off the Unity main thread. Main-thread handlers must start asynchronous work and return control to the Editor quickly so compilation, updates, callbacks, and reload can proceed.
- A worker-thread poll is safe only when it reads immutable or synchronized plain managed snapshots and filesystem state. Do not let it call Unity APIs or deserialize through Unity while another domain transition may be occurring.
- Before reload or shutdown, stop the listener, signal all waiting request threads, and close every active client. A blocked socket thread must not delay domain teardown or emit a fabricated protocol error after shutdown begins.
- A broken connection is evidence of transport interruption, not evidence that the underlying Unity operation failed. The client should rediscover the endpoint and poll by operation ID.
- A local endpoint may disappear during reload and be rebound in the next domain. Port files must be written atomically, and clients must not assume that a connection or port remains valid.
- Avoid blocking timeouts as a substitute for lifecycle state. Use explicit signals and durable outcomes; reserve bounded timeouts for permanent unavailability, startup failure, or infrastructure failure.
- Do not run two integration suites against the same Unity project concurrently. They share the Editor, project lock, scripts, `Temp` protocol files, and generated fixtures, so concurrency invalidates test isolation and can corrupt teardown assumptions.

## Compilation, refresh, and diagnostics

- `AssetDatabase.Refresh()` and `CompilationPipeline.RequestScriptCompilation()` start asynchronous Editor work and may cause compilation, domain reload, or both. Persist the request before invoking them and resume observation in the next domain.
- `EditorApplication.isCompiling`, `EditorApplication.isUpdating`, and related flags are transient observations, not durable operation state. They can be false before work starts and briefly between lifecycle phases.
- Do not declare refresh complete on the first idle observation. Require the request flags to clear and observe a settled Editor on multiple update ticks so a not-yet-started compilation is not mistaken for completion.
- Compiler diagnostics must be captured at `CompilationPipeline.assemblyCompilationFinished`, before a possible reload. `compilationFinished` or a later Editor update may be too late for warnings from the old domain.
- Persist captured diagnostics atomically and preserve them when the next domain is resuming the same refresh/recompile operation. An empty new-domain static collection must not erase valid old-domain diagnostics.
- Compilation completion, diagnostic publication, and Editor settling are distinct moments. Produce the final command result only after the Editor settles, but capture each assembly's diagnostics as soon as Unity publishes them.
- Compiler warning availability and formatting vary across Unity and compiler versions. Integration tests should generate a deterministic warning known to exist on the pinned version and assert that warning, rather than depend on incidental Unity warnings.
- Stack-trace IL offsets can vary across compiler versions. Normalize only the unstable portion and scope normalization to the intended fixture; broad normalization can hide meaningful regressions in unrelated output.

## Unity Test Framework

- `TestRunnerApi.Execute()` is asynchronous. `IsRunActive()` can be false before `RunStarted` and at other lifecycle boundaries, so it must not be used alone to infer cancellation or completion.
- Test Framework callbacks are authoritative for normal start, completion, failure, and cancellation. Bind callbacks to the exact run ID and verify durable ownership before writing results.
- Do not destroy or unregister Test Framework objects owned by other tools. UnityCLI should identify and clean up only the callback owner it created.
- A successful `CancelTestRun` return value means cancellation was accepted, not that a terminal callback is guaranteed. Some Unity/Test Framework versions can remain in `Cancelling`; cancellation handling needs a bounded interruption path and must not be the sole basis of a reload-resilience test.
- Avoid tests that invoke private Test Framework cancellation internals through reflection. They validate undocumented framework behavior, can hang indefinitely across versions, and do not prove UnityCLI's interruption recovery.
- A test run may complete while the Editor is also reloading or shutting down. Write a terminal result only if the callback still owns that run, and let lifecycle recovery handle the otherwise interrupted transport.

## Filesystem and cross-platform behavior

- Derive the project root from `Application.dataPath`, not `Directory.GetCurrentDirectory()`. User code, launchers, IDEs, and operating systems can supply or change an unrelated working directory.
- Write durable state and result files atomically using a uniquely named temporary file in the destination directory, then replace or move it into place. Same-directory writes avoid cross-volume rename behavior.
- On Windows (NTFS/Win32), `File.Replace` (`ReplaceFileW`) and `File.Delete` have mandatory file-locking and `DELETE_PENDING` semantics, unlike POSIX `rename(2)` / unlinking on macOS and Linux. Concurrent readers or polling probes can observe transient sharing violations (`IOException`) or 0-byte reads during replacement and deletion. Cross-platform atomic file writers should include a short retry loop with backoff to handle transient read locks.
- Keep protocol filenames project-scoped and avoid unresolved environment variables, shell-specific path assumptions, or case-sensitive comparisons. Test paths containing spaces and both slash conventions.
- Unity lockfiles are platform-dependent. They may be empty, expose no owner, disappear briefly during startup/reload, or remain stale after an abnormal termination. Treat them as supporting evidence, not authoritative liveness.
- Process-image scans are unreliable when multiple projects or Unity versions are open. Prefer a project-scoped handshake and verified endpoint.
- Pin both the Unity version and revision in `ProjectVersion.txt`, and run compatibility tests against installed versions deliberately. Opening a project in a different version can rewrite project settings and change compiler/test output independently of UnityCLI.
- Test Windows, Linux, and macOS behavior explicitly. File replacement, lockfiles, process discovery, socket teardown, line endings, executable lookup, and shell quoting all have platform-specific failure modes.
- Shell scripts across Linux, macOS, and Windows Git Bash must never declare function-only keywords such as `local` in top-level script scope. POSIX shell interpreters produce runtime syntax failures when encountering `local` outside function definitions during fallback execution paths.

## Test and maintenance discipline

- Integration fixtures that modify project files must save the original bytes and restore them on success, failure, signal, and early exit. Cleanup should be ownership-aware and must never overwrite a user's concurrent edit.
- Keep full-suite execution single-owner, but use focused cases during debugging. Before starting a new run, confirm that no prior Unity/test process is still active.
- Generated Unity files and settings rewrites should be removed from the worktree after validation. Review `git status` and `git diff --check` so test artifacts, line-ending changes, and Unity migrations are not committed accidentally.
- Golden-output normalization should be narrowly targeted to known nondeterminism such as duration, project path, or a fixture-specific IL offset. Never normalize error categories, operation IDs, or other values that establish protocol correctness.
- Reliability tests should assert durable externally observable outcomes: exact operation correlation, atomic results, recovery after a lost connection, busy rejection, and successful continuation after a real domain reload.
