# Development Guidelines

- Design every change to work reliably on Windows, Linux, and macOS.
- Prefer simple, transactional designs. Keep operations easy to reason about and recover from so that complexity does not introduce subtle, difficult-to-diagnose bugs.
- Treat Unity's domain reload as asynchronous and externally triggered. Unity may reload the domain at any time—not only in response to UnityCLI—because the user can interact with the Unity Editor while an operation is running, or Unity can detect script changes in the background. Code must therefore tolerate interruption and reinitialization without relying on uninterrupted process state.
