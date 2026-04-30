# Step: Diff Local vs Remote

Your goal is to show the user exactly what will change on the FTP server before
any files are transferred.

## Instructions

1. Run the diff script to compare local output against the remote server:

   ```bash
   bash {{playbook.base-path}}/scripts/diff.sh
   ```

   The script will output a categorised file list to stdout.

2. Parse the output into three buckets:
   - **NEW** — files that exist locally but not on the server
   - **MODIFIED** — files that exist on both sides but differ (by size or mtime)
   - **UNCHANGED** — files identical on both sides (will be skipped)

3. Check for files that should never be uploaded (constraint enforcement):
   - `.env`, `.env.*`, `*.pem`, `*.key`, `*.p12`, `credentials.*`
   - If any are found in the NEW or MODIFIED lists, abort immediately and list them.

## Output

Present the manifest clearly before the Review gate:

```text
Remote host    : {{params.host}}
Remote path    : {{params.remote-path}}
Local path     : {{state.build_path}}

── Files to upload ──────────────────────────────
  NEW       : <count> files
  MODIFIED  : <count> files
  UNCHANGED : <count> files (will be skipped)
  TOTAL     : <count> files to transfer

── New files ────────────────────────────────────
  + path/to/new-file.js        (12 KB)
  + path/to/another.css        (3 KB)
  ...

── Modified files ───────────────────────────────
  ~ path/to/changed.html       (8 KB → 9 KB)
  ...

── Skipped (unchanged) ──────────────────────────
  = path/to/image.png          (unchanged)
  ...
```

Store the upload manifest (new + modified file paths) as `upload_manifest`.

If there is nothing to upload (all files unchanged), tell the user and end the
playbook — do not proceed to the upload step.
