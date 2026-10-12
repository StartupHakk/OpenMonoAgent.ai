## Summary

This PR updates `docker/Dockerfile.agent` to install the native build dependencies required by Python packages installed during the agent image build.

The agent image currently installs `code-review-graph` and `graphifyy` via `pip3`. Their dependency tree can pull in native Python packages such as `tree-sitter-dm`, which may need to compile a Python extension during image build.

Without the compiler toolchain and Python development headers installed in the runtime image, the build can fail during the `pip3 install` step.

## What changed

Added the following packages to the runtime image apt install block:

* `build-essential`
* `python3-dev`

## Why

The Docker image build failed while building the `tree-sitter-dm` wheel, first because the C compiler was unavailable:

```text
error: command 'x86_64-linux-gnu-gcc' failed: No such file or directory
```

After adding compiler tooling locally, the build progressed but then failed because Python development headers were unavailable:

```text
fatal error: Python.h: No such file or directory
```

Installing `build-essential` provides the compiler toolchain, and installing `python3-dev` provides the Python headers needed to build native Python extensions.

## Testing

Tested by rebuilding the agent image with:

```bash
docker compose build --no-cache --progress=plain agent
```

The change is intentionally limited to the Docker runtime build dependencies needed for the existing Python package installation step.

