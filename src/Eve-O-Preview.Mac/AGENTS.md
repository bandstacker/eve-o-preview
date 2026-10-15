# macOS host

Read [the source instructions](../AGENTS.md) and the [macOS port guide](../docs/ai/macos-port.md) first.

- This project links upstream `../Eve-O-Preview` sources; it does not copy them. Prefer a macOS implementation behind an existing interface, or a same-named type in `Compat/`, over editing shared files. When a shared edit is unavoidable, keep it small and platform-guarded and add it to the guide's seam table.
- Preview geometry is Quartz points (`PreviewPixels.AreDips`). Client window handles are `CGWindowID`s; own window handles are `NSWindow*`. Never mix them.
- Native calls go through `Native/MacNative.cs` and the Swift `eom_*` C ABI. AppKit work in Swift must run on the main thread (`onMain`); callbacks into .NET must stay short and post to the UI thread.
- Hidden settings and macOS replacements are configured in `MacProgram.ConfigurePlatform`; macOS-only values live in `MacSettings`, never in gameplay profiles.
- Verify behavior with `--debug-harness` and the mock client (`../tests/Eve-O-Mock.Mac`), run `../tests/Eve-O-Preview.Mac.Tests`, and compile the Windows host with `-p:EnableWindowsTargeting=true` after shared edits. Report what was validated against the mock versus a real EVE client.
