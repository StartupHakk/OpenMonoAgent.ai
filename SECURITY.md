# Security Policy

## Supported versions

Security fixes are prioritized for the latest release on the `main` branch of [OpenMonoAgent.ai](https://github.com/StartupHakk/OpenMonoAgent.ai). If you are running an older build, please upgrade before reporting, or note the exact version / commit in your report.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Prefer one of these private channels:

1. **GitHub private vulnerability reporting** (preferred when enabled): use **Security → Report a vulnerability** on this repository.
2. **Email:** spencer@startuphakk.com with subject line starting with `[SECURITY]`.

Include as much of the following as you can:

- Description of the issue and potential impact
- Steps to reproduce (PoC, commands, configs)
- Affected component (agent, Docker sandbox, VS Code extension, install scripts, relay / inference path, docs tooling, etc.)
- Version, commit SHA, OS, and whether you used local inference or the optional secure relay
- Whether you believe the issue is already being exploited

You should receive an acknowledgment within **3 business days**. We aim to provide a status update within **10 business days** after acknowledgment. Complex issues may take longer; we will keep you informed.

## Scope

In scope examples:

- Remote or local code execution via the agent, tools, MCP integrations, or installers
- Docker sandbox escape or unintended host access
- Privilege escalation, path traversal, or unsafe defaults that bypass permission gates
- Secrets leakage (tokens, keys, transcripts) through logs, telemetry, or network paths
- Issues in the optional secure anonymous inference relay path that weaken confidentiality, integrity, or the no-store / no-train commitments
- Supply-chain issues in release artifacts, install scripts, or dependency pinning that a maintainer can reasonably fix in this repo

Out of scope examples (still welcome as normal issues or discussions when appropriate):

- Vulnerabilities only in third-party models, GPUs, or host OS configuration outside our control
- Denial of service that requires already having full local control of the machine
- Social engineering of end users
- Reports that require privileged access you already have on your own machine with no additional impact

## Safe harbor

We will not pursue legal action against researchers who:

- Make a good-faith effort to avoid privacy violations, destruction of data, and interruption of service
- Do not exploit the issue beyond what is needed to demonstrate it
- Report the issue privately and give us a reasonable time to fix before public disclosure
- Do not use the finding for anything illegal

## Disclosure

We prefer coordinated disclosure. Please do not publish exploit details until we have shipped a fix or agreed on a disclosure date, except where required by law or by a clear, ongoing active threat that cannot wait.

## Security posture (high level)

OpenMonoAgent is designed so that:

- The coding agent and project working tree stay under the user's control
- Destructive or sensitive tool actions should go through permission gates
- Agent execution prefers Docker sandbox isolation by default
- Optional inference via StartupHakk-hosted hardware uses a secure anonymous relay path intended **not to store or train on user data**

Threat modeling and hardening of these boundaries is ongoing. Security reports help that work.

## Preferred languages

English is preferred for security reports.
