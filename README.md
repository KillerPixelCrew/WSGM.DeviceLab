# WSGM Device Lab

The authoring and diagnostic tool for [WSGM Device Plugins](https://github.com/KillerPixelCrew/WSGM.Device.Sdk).
It inventories the handheld you are targeting, captures what its hardware actually does, scaffolds a
plugin from that capture, validates and packs the package, and runs the attended hardware tests that
only a real machine can answer.

A GUI and a CLI over the same code — `wsgm-device` is the executable.

## Why it is a separate tool

Writing a device plugin means answering questions about a specific machine that no documentation
will tell you: which EC register the fan curve lives behind, what the OEM button reports, whether a
power-limit write actually took. Device Lab exists to answer those **before** you write the plugin,
and to prove the answers afterwards.

It is deliberately not part of WSGM. WSGM ships to end users and owns a live session; Device Lab is
a developer tool that runs offline, on a machine that may not have WSGM installed at all.

## The workflow

```powershell
# 1. What is this machine?
wsgm-device doctor    --out-dir diagnostics
wsgm-device inventory --out-dir inventory --shareable

# 2. Capture it, then read what you captured.
wsgm-device inspect    capture.wsgmcap
wsgm-device compare    before.wsgmcap after.wsgmcap
wsgm-device correlate  capture.wsgmcap --action <id> --sources <id,id>

# 3. Turn a capture into a buildable plugin.
wsgm-device scaffold --from capture.wsgmcap --out-dir my-plugin `
    --usb-instance <exact-instance-id> # required only when several exact USB endpoints are present

# 4. Prove it, offline first.
wsgm-device validate my-plugin
wsgm-device test sample
wsgm-device test plugin my-plugin --from inventory.json

# 5. Ship it.
wsgm-device pack my-plugin --out plugin.wsgmpkg
```

`inventory --shareable` is the form meant for a bug report: it carries the device facts and drops
the identifying ones.

## Attended versus unattended

The split is enforced, not advisory.

- **`validate`, `inspect`, `compare`, `correlate`, `inventory`, `doctor`, `pack`** are read-only or
  offline. `validate` never loads plugin code — it checks the manifest, the package layout and that
  the entry assembly is a managed x64 image, all statically.
- **`test hardware`** writes to the device, so it demands an explicit action, a state directory you
  named, and your presence. It exists because a capability write is only proven on real hardware.
  Detection and the entire attended lifecycle run in an authenticated disposable worker process;
  Device Lab kills its full process tree at the hard deadline and keeps the production owner slot
  reserved when cleanup was not verified.

Capture export displays a bounded projection of the actual sanitized bundle before confirmation:
the root documents are shown, every stream/analysis/blob is represented by exact counts and hashes,
and large content is sampled instead of silently omitted from the privacy review.

Output paths are checked before anything is written: a broad home directory, a repository root, or
an existing reparse point is refused rather than written into.

## Scaffolded plugins are yours

`scaffold` generates a plugin that links only `WSGM.Device.Sdk`, which is MIT. It ships an MIT
`LICENSE.txt` with a placeholder for your name because that constrains you least — replace it with
whatever licence you want, including none of these. WSGM itself is GPL-3.0-or-later, but a plugin
does not link WSGM.

## Building

```powershell
git clone --recursive https://github.com/KillerPixelCrew/WSGM.DeviceLab
dotnet build WSGM.DeviceLab.slnx
dotnet test  WSGM.DeviceLab.slnx
```

`--recursive` matters: `external/WSGM.Device.Sdk` is a submodule, and without it the build fails on
an unresolvable project path rather than saying what is missing.

Device Lab pins an exact SDK revision, and the plugins it scaffolds reference **that exact SDK** —
inside a checkout as a project reference, and from an installed copy as an explicit reference to
the `WSGM.Device.Sdk.dll` shipped beside the tool. A plugin built against a contract the host does
not have is the failure this avoids.

## Licence

MIT. See `LICENSE`. Third-party components it redistributes keep their own licences; see
`src/WSGM.DeviceLab/THIRD_PARTY_NOTICES.md`.
