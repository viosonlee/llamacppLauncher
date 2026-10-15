# LlamaManager — Agent Instructions

## Discovery

Prefer codebase-memory MCP graph tools for code discovery when available.
Use search_graph, trace_path, get_code_snippet, query_graph and get_architecture.
Fall back to text/file search when tools are unavailable, results are insufficient,
or searching configuration, documentation, literals and logs.

## Build and verify

Requires Windows x64 and .NET 8 SDK; project targets `net8.0-windows` with WPF.
Resources are defined in `gui/LlamaManager/Brushes.xaml` and merged by App.xaml.

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -r win-x64 --self-contained true -o dist
```

The regression suite is a console runner, not a dotnet-test project. It uses an isolated
LLAMA_MANAGER_ROOT, generated GGUF headers and local fake servers, without GPU/model downloads.
Publish to a separate output folder when an existing installation is in use.

## Structure

- MainWindow.xaml.cs: initialization and settings persistence.
- MainWindow.Parameters.cs: field-level edits and parameter explanations.
- MainWindow.Models.cs: directory scans and model/preset selection.
- MainWindow.Runtime.cs: status polling, lifecycle actions and speed test UI.
- Services/ServerService.cs: shared main and embedding lifecycle.
- Services/ConfigStore.cs: atomic transactions and corruption recovery.
- Services/GgufService.cs: metadata and tensor descriptor inspection.
- Services/CommandLineService.cs: headless commands used by autostart and legacy wrappers.
- Tests and architecture notes: README.md and docs/architecture.md.

## Constraints

Read the current config when troubleshooting, but do not stage user configuration,
logs, downloaded engines, models, build outputs or unrelated shortcut changes.
Never stop a user's real model service for ordinary regression tests.

A running ServerSession is independent from the selected model and editable arguments.
Validate PID, process creation time and engine path before managing a process.
Settings saves must not overwrite live session identity; runtime updates must not overwrite presets.
Presets use normalized absolute-path keys and sampled file signatures.
Use System.IO for paths; JSON accepts escaped backslashes as well as forward slashes.

Keep GUI, CLI and autostart on the shared C# services. Do not duplicate lifecycle logic in PowerShell.
Save PowerShell scripts as UTF-8 with BOM for Windows PowerShell 5.1 compatibility.
Keep device probes cached and bounded; no blocking HTTP or subprocess waits on the UI dispatcher.
Use tests that exercise behavior, including failure/cancellation, when changing these paths.
