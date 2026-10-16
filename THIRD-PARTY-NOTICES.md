# Third-Party Notices

This launcher can drive several external inference engines. It does not bundle their
binaries or models. The one-click "初始化环境" (`--bootstrap`) downloads an engine from its
official GitHub release on demand; each engine keeps its own licence.

## llama.cpp (ggml-org/llama.cpp)

- Licence: MIT
- Used by: the built-in `llamacpp` backend. The bootstrap downloads a Windows CUDA build
  (`llama-<build>-bin-win-cuda-*.zip`) and its CUDA runtime (`cudart-llama-*.zip`) from
  https://github.com/ggml-org/llama.cpp/releases. The launcher only launches `llama-server.exe`.

## Strata (Niko1221/Strata)

- Licence: MIT
- Copyright (c) 2026 Niko1221 and the Strata contributors
- Used by: the built-in `strata-upstream` backend. The bootstrap downloads the engine
  (`strata-windows-x64.zip`) and the matching source archive (`serve/server.py`,
  `requirements.txt`) from https://github.com/Niko1221/Strata/releases, then starts the
  engine's own HTTP service via a local Python virtual environment.

## PolyStrata (VecSzn/PolyStrata)

- Licence: MIT (based on Strata)
- Used by: the built-in `polystrata` backend. The bootstrap downloads the engine
  (`strata-poly-windows-x64-<hash>.zip`) and the matching source archive from
  https://github.com/VecSzn/PolyStrata/releases. Like Strata, its HTTP API comes from
  `serve/server.py`; the engine executable speaks a line protocol.

## Python packages

The Strata-family services install their Python dependencies (numpy, jinja2, regex, pyyaml,
tqdm, requests, pillow, psutil, and their transitive dependencies) into a local virtual
environment. Versions come from each engine's own `requirements.txt`; each package keeps its
own licence.
