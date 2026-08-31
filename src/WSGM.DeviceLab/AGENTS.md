# WSGM.DeviceLab

The separate Device Lab application owns inventory, known-device comparison, passive
capture, compiled read probes, scaffolding, local plugin testing, glyph import, validation, and
packaging. Its no-argument and `gui` modes start Avalonia; every documented command is routed by the
same console-subsystem executable. Keep the GUI and CLI thin over shared application services.

- **Read-only is the default and mutation is one named door.** Inventory, comparison, capture,
  inspection, fixture extraction, scaffolding, offline validation, and packing cannot touch hardware.
  The explicit attended plugin test has no unattended, bulk, remembered-consent, or CI path.
- Imported captures, manifests, packages, and request files never define an arbitrary hardware
  operation. Never disable Device Integration or race the production plugin.
- Read probes are compiled, typed, exact-device matched, rate-limited, deadline-bounded,
  response-validated, and run in the disposable hidden self-worker mode. That mode
  must not become a generic device-access broker or production runtime protocol.
- The self-worker process is expected to be killed. It owns no durable state and must leave nothing
  behind that a crash could strand.
- Every output path is explicit. Reject the live `%LOCALAPPDATA%\WSGM` directory, repository root,
  and broad home paths. Never infer an output from the current directory.
- Shared workflow output is deterministic. Keep imported-data validation and redaction in shared
  services so CLI and GUI behavior cannot drift.
- CLI results go to stdout, diagnostics to stderr, and exit codes retain `0` success and `64` usage.
  The attended mutation command remains hostile to automation and never accepts `--yes`.
- GUI work stays cancellable and off the UI thread. Marshal immutable results back to Avalonia,
  prevent duplicate submissions, and preserve the last successful result when a later run fails.
- The one attended plugin action requires immediate local confirmation, refuses CI and `--yes`,
  executes only the selected semantic capability, haptic, or controller action, verifies its
  restore/release, and always calls plugin cleanup after activation begins.
- Resolve the state path, elevation, local-attendance, CI, and confirmation gates, then atomically
  reserve the exact machine-wide production-owner object before loading plugin code. Keep that
  handle-held, unowned reservation through cleanup and plugin disposal, retaining it for process
  lifetime when construction or disposal leaves cleanup unverified; never wait on or release the
  mutex across async work. Exact detection is the only dynamic gate between loading and activation.
- Label observation limits accurately: timing correlation is a candidate, a nonempty response is not
  proof, and user-mode observers cannot infer another process's exact device operation.
