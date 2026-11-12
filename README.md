# Editor

A plain text editor for Windows 10 22H2 and Windows 11. The working name is a placeholder.

The text engine (`src/Editor.Core`) is a .NET 8 library with no UI. It builds and tests on Linux and Windows. The window (`src/Editor.App`) is a WinUI 3 single-project MSIX app with a Win2D text surface, for x64 and ARM64.

## Layout

- `src/Editor.Core` — piece table, undo, line index, encodings, line endings, find and replace, atomic save
- `src/Editor.App` — one document window
- `tests/Editor.Core.Tests` — unit tests, including a feature-flag matrix
- `tests/Editor.Core.PerfTests` — open, memory, and insert gates
- `docs/architecture.md` — how the pieces fit
- `docs/renderer-poc.md` — text pattern, text input, and the grapheme caret
- `docs/m1-acceptance.md` — what to try on a Windows machine

## Build and test

The engine:

```bash
dotnet test tests/Editor.Core.Tests/Editor.Core.Tests.csproj -c Release
```

The Windows app needs the Windows App SDK and is built on Windows:

```powershell
dotnet publish src/Editor.App/Editor.App.csproj -c Release -r win-x64 -p:Platform=x64
```

`EDITOR_DISABLE_FEATURES` changes the default settings used when no settings file is present. Use `none`, `*`, or a comma-separated list such as `findReplace,statusBar`.

Feature flags, all read from `features.<name>`:

| Flag | What it turns off |
| --- | --- |
| `findReplace` | Find and replace |
| `goToLine` | Go to line |
| `statusBar` | The status bar |
| `atomicSave` | Temp-file replace (the save still refuses a read-only target) |
| `encodingDetection` | UTF-8 sniffing (a BOM is still honored) |

On Windows the app reads `%LocalAppData%\Editor\settings.json`.

## Continuous integration

GitHub Actions runs the unit tests on Linux, repeats them with each feature name in `EDITOR_DISABLE_FEATURES`, runs the performance gates on Windows, and publishes an unsigned MSIX plus a self-contained portable zip for `win-x64` and `win-arm64`. A test signature is attempted and is not required. Cold start is measured when the runner can open a window, and a miss is recorded rather than failed.

Performance gates, measured in the core library without a window:

| Check | Limit |
| --- | --- |
| Open a 100 MB UTF-8 file and lay out the first page | 300 ms |
| Managed memory for that open | 2.5× the file |
| Open a 1 GB file without indexing it | 1500 ms, under 1 MB scanned |
| Insert in the middle of that 1 GB document | 16 ms median |
| Cold start to a usable window | recorded, not gated |

## License

All rights reserved, Hari Kishore Chada.
