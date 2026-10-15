# Third-Party Notices

This launcher can drive several external inference engines. It does not bundle their
binaries or models by default; the one-click bootstrap locates or installs them, and
each engine keeps its own licence.

## Strata (Niko1221/Strata)

- Licence: MIT
- Copyright (c) 2026 Niko1221 and the Strata contributors
- Used by: the `strata-upstream` backend. The launcher generates a run config and starts
  the engine's own HTTP service (`serve/server.py`); the engine source and binaries are
  the user's own install.

## PolyStrata (VecSzn/PolyStrata)

- Licence: MIT
- Used by: the bundled `polystrata` plugin manifest. The manifest only declares how to
  launch the engine; `strata-poly` itself is installed separately by the user.

## llama.cpp (ggml-org/llama.cpp)

- Licence: MIT
- Used by: the built-in `llamacpp` backend via `llama-server`.

## Python packages installed by the bootstrap

The bootstrap installs the packages pinned by the Strata source tree's `requirements.txt`
(numpy, jinja2, regex, pyyaml, tqdm, requests, pillow, psutil and their dependencies) into
a local virtual environment. Their licences are the packages' own.

Bundled plugin manifests are small JSON files and carry the same MIT terms as the engines
they describe.
