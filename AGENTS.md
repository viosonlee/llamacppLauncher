# LlamaManager - Agent Instructions

## Quick Start Commands

```bash
# Build and run
dotnet build --configuration Release
dotnet run --project gui\LlamaManager --configuration Release

# Publish as standalone executable
dotnet publish --configuration Release -r win-x64 --self-contained true --no-build -o dist
```

## Project Structure

```
llama-manager/
├── gui/                    # WPF application source
│   └── LlamaManager/
│       ├── Models/         # Data models (ModelInfo, ManagerConfig)
│       ├── Services/       # Business logic (Server, Preset, Log, Config, Cuda)
│       ├── MainWindow.xaml # UI layout
│       └── LlamaManager.csproj
├── dist/                   # Published executables
├── logs/                   # Runtime logs
└── *.config.json           # User configuration
```

## Key Facts

**Framework**: .NET 8.0 WPF (targeting `net10.0-windows` in csproj)
**Build Artifacts**: Compiled to single executable in `dist/`
**Config File**: `llama-manager.config.json` (user preferences, model presets)

**Service Classes**:
- `ServerService.cs` - Controls llama.cpp server process
- `PresetService.cs` - Manages model presets from config
- `LogService.cs` - Writes console output to UI
- `CudaService.cs` - Detects NVIDIA CUDA availability
- `EmbeddingService.cs` - Starts BGE-M3 embedding server (port 8081)

**Default Settings**:
- Model path: `C:\Users\li.feng\.llama\models\HauhauCS\mini-coder-4b\mini-coder-4b-q8_0.gguf`
- Server port: 8080
- Embedding port: 8081
- CPU cores: 8

## Dev Workflow

1. **First-time setup**: Read `llama-manager.config.json` to understand current model and preset configuration
2. **Debug**: Use `dotnet run` with default config; inspect `Models/` and `Services/` for logic
3. **Testing**: No unit tests exist; verify by running app and checking `logs/`
4. **Deployment**: Run `dotnet publish` to create standalone `.exe` in `dist/`

## Important Constraints

- Self-contained single-file deployment requires all .NET runtime DLLs bundled
- WPF resources defined in `Resources.xaml` (brushes, colors)
- Config paths use absolute paths with forward slashes (JSON format compatibility)
- Preset models are keyed by full filesystem path, not name

## Known Issues / Gotchas

- Project targets `net10.0` (preview); verify .NET 10.0 SDK is installed
- `obj/` and `bin/` contain compiled artifacts; clean with `dotnet clean` before rebuild
- Embedded resources (e.g., `Resources.xaml`) may need special handling in publish
