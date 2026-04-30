---
name: deploy-ftp
version: 1.0.0
description: >
  Build the project, diff local output against the remote FTP server, and upload
  only changed files. Pauses for user review before any files are transferred.
trigger: manual
trigger-patterns:
  - "deploy *"
  - "ftp deploy *"
  - "upload to *"
  - "deploy to ftp"
  - "push to server"
user-invocable: true
argument-hint: "<host> <user> [--remote-path /public_html] [--local-path ./dist] [--dry-run true]"

parameters:
  host:
    type: String
    required: true
    hint: "FTP hostname or IP address (e.g. ftp.example.com)"
  user:
    type: String
    required: true
    hint: "FTP username"
  remote-path:
    type: String
    required: false
    default: "/public_html"
    hint: "Absolute path on the remote server to deploy into"
  local-path:
    type: String
    required: false
    default: "./dist"
    hint: "Local directory to upload (must exist after the build step)"
  build-command:
    type: String
    required: false
    default: "npm run build"
    hint: "Command to produce the build output. Set to 'none' to skip the build step."
  dry-run:
    type: Boolean
    required: false
    default: false
    hint: "Show what would be uploaded without transferring any files"
  passive-mode:
    type: Boolean
    required: false
    default: true
    hint: "Use FTP passive mode (recommended for most firewalls)"

allowed-tools:
  - Shell
  - ReadFile
  - WriteFile
  - Glob

context-mode: Selective
max-context-tokens: 4000
depends-on: []

tags:
  - deploy
  - ftp
  - upload

constraints:
  inline:
    - "Never upload .env, .env.*, *.pem, *.key, *.p12, or credentials.* files under any circumstances."
    - "Never read or print the FTP_PASSWORD environment variable in any output."
    - "Never delete remote files unless the user explicitly passed --delete true."
    - "Never proceed past the diff step without an explicit user approval at the gate."
    - "Never run the build command if build-command parameter is 'none' — skip directly to diff."
    - "Always use passive mode unless the user explicitly set passive-mode to false."
    - "If dry-run is true, print a summary of what would happen but do not transfer any files."

steps:
  - id: pre-flight
    inline-prompt: >
      Run {{playbook.base-path}}/scripts/pre-flight.sh and report its output verbatim.
      If the script exits non-zero, halt the playbook immediately and explain
      which check failed and how to fix it.
    script: scripts/pre-flight.sh
    gate: None

  - id: build
    requires: [pre-flight]
    file: steps/01-build.md
    gate: None
    output: build_path

  - id: diff
    requires: [build]
    file: steps/02-diff.md
    gate: Review
    output: upload_manifest

  - id: upload
    requires: [diff]
    inline-prompt: >
      Run {{playbook.base-path}}/scripts/upload.sh to transfer files to the FTP server.
      Pass the following as environment variables (do NOT print them):
        FTP_HOST={{params.host}}
        FTP_USER={{params.user}}
        FTP_PASSWORD is already in the environment.
        REMOTE_PATH={{params.remote-path}}
        LOCAL_PATH={{state.build_path}}
        DRY_RUN={{params.dry-run}}
        PASSIVE={{params.passive-mode}}
      Report each uploaded file, total file count, total bytes transferred,
      and elapsed time. If dry-run is true, print the dry-run summary instead.
    script: scripts/upload.sh
    gate: Approve
---

You are a deployment assistant. Your job is to safely build the project,
show the user exactly what will change on the server, wait for their approval,
then upload only the necessary files via FTP.

Speak in concise, direct language. No unnecessary caveats or padding.
Always log every shell command you run and its exit code.

## Step responsibilities

### pre-flight
Verify the local environment is ready: required tools present, FTP credentials
in environment, local build directory accessible.

### build
Run the configured build command and confirm the output directory exists and
is non-empty. If `build-command` is `none`, skip this step and confirm the
local directory already exists.

### diff
Connect to the FTP server and compare the local build output against the remote
path. Produce a clear manifest: new files, modified files, unchanged files.
Present totals before asking the user to approve.

### upload
Transfer only new and modified files. Report progress. If `dry-run` is true,
print what would be transferred without connecting to the server.
