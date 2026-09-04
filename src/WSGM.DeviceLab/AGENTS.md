# WSGM Device Lab contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.DeviceLab/**`.

Device Lab is a separate MIT-licensed Windows authoring and diagnostics application, not part of the
WSGM runtime. The Avalonia GUI and `wsgm-device` CLI must use the same application services and
produce the same results. Before changing behavior, read the repository `README.md`, the relevant
tests, and the implementation being changed; keep all three aligned.

## Build and dependency workflow

From the repository root:

```powershell
dotnet build WSGM.DeviceLab.slnx --configuration Release
dotnet test WSGM.DeviceLab.slnx --configuration Release --no-build
```

The target is .NET 10 on Windows; release publishing is self-contained `win-x64`. Run
`./eng/publish.ps1` only when a publish artifact is requested. The project version in
`WSGM.DeviceLab.csproj` is authoritative. Do not tag, release, or publish unless explicitly asked.

`external/WSGM.Device.Sdk` is a pinned source dependency. Inspect `git submodule status --recursive`
before changing it, and initialize or synchronize only to the intended recorded commit when the
checkout has no local submodule work. Keep the gitlink reproducible; never float it to an unreviewed
branch or package. Scaffolding references the checked-out SDK project in a source checkout, or the
exact SDK assembly beside the running tool otherwise.

## Command and application contract

- No arguments and `gui` start the GUI. Normal CLI commands are `doctor`, `inventory`, `candidates`,
  `probe-read`, `capture`, `inspect`, `compare`, `correlate`, `fixture`, `scaffold`, `glyph`,
  `validate`, `test`, and `pack`.
- `__read-probe` and `__plugin-test` are authenticated internal worker modes, not public commands.
- Use stdout for result JSON and stderr for diagnostics. Preserve exit codes: `0` success, `64`
  usage error, `70` operational failure.
- Keep long work cancellable and off the UI thread. Reject duplicate GUI operations, and do not
  erase the last successful result when a later operation fails or is cancelled.
- Add command behavior through `Application/` and the shared CLI/GUI services rather than parallel
  implementations.

## Safety boundary

Device Lab's built-in workflows are read-only by default. Imported inventory, recipes, fixtures, and
packages are untrusted evidence; use static validation until plugin code has been deliberately
trusted.

- Compiled read probes require an exact live device and endpoint match, typed expectations and
  cross-checks, strict time/read limits, an authenticated one-use worker, and process-tree
  termination at the deadline. They must not write hardware or durable state.
- `test plugin` loads, constructs, and calls arbitrary plugin code with the user's authority. Its
  authenticated worker and job object contain crashes and deadlines; they are not a security sandbox
  or hardware-access boundary.
- `test hardware` is the only built-in workflow that intentionally requests a plugin mutation. It
  must be a local attended session, reject CI and non-interactive use, reject every form of `--yes`,
  require immediate confirmation of one explicit semantic action, recollect live identity, and use a
  new explicit state directory. Preserve the static refusal path before plugin code is loaded, while
  remembering that a malicious plugin can ignore the SDK contract once executed.
- Reserve the unowned `Global\WSGM.DeviceOwner` mutex before loading a hardware plugin. Hold it
  through cleanup and disposal; never wait on or release it across `await`. If construction, package
  identity, stop, or disposal is unverified, retain ownership for the process lifetime.
- A hardware action must capture original state, apply one action, verify readback, and restore/zero
  output/release before success. An unverified cleanup is a failure.
- Observe-only capture requires hash-bound approval of the local interactive observation scope, then
  a separate approval of the sanitized export preview before publication. Do not merge or bypass
  those approvals.

## Filesystem and artifact rules

- User-created workflow artifacts use new, non-reparse, owned output targets. The marked publish
  tree managed by `eng/publish.ps1` is the explicit exception and may be atomically replaced after
  ownership checks. Reject drive and filesystem roots, the broad home directories themselves, the
  repository root itself, and the live `%LOCALAPPDATA%\WSGM` tree, including its descendants.
- Use staging plus atomic publication and create-new semantics. Never overwrite an unrelated target
  or follow a reparse point.
- Keep private captures separate from shareable redacted output. Preserve deterministic hashing,
  archive ordering, retained-input evidence, and preview/count/hash consistency.
- Treat correlation as bounded candidate evidence, never proof of causation.

## Package and scaffold rules

- Package validation is static and must never load plugin code. Keep manifest/layout, managed-x64
  PE, entry-count, file-size, aggregate-size, and prohibited-file checks bounded and fail closed.
- Retain opened input handles through validation and packing so validated bytes are the bytes
  published. Keep packages deterministic.
- Generated projects, manifests, tests, glyph profiles, and documentation must agree on package ID,
  API version, target framework, and SDK reference.

## Change discipline

Prefer semantic records and deterministic services over device-specific special cases. Keep hardware
policy out of UI code. Update focused unit tests for every changed invariant, especially refusal
paths, worker authentication/deadlines, mutex lifetime, path ownership, package limits,
deterministic output, and cancellation. Preserve the repository `.editorconfig` conventions.
