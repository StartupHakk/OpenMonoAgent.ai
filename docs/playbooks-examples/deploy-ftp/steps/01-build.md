# Step: Build

Your goal is to produce the deployable output in `{{params.local-path}}`.

## Instructions

1. If `{{params.build-command}}` is `none`, skip to step 3.

2. Run the build command from the workspace root:
   ```
   {{params.build-command}}
   ```
   Capture stdout and stderr. If the command exits non-zero, abort the playbook
   with the full error output — do not proceed to diff or upload.

3. Verify the output directory exists and is non-empty:
   ```
   ls -lh {{params.local-path}} | head -20
   ```
   If the directory is missing or empty, abort with a clear message explaining
   that the build produced no output.

4. Count the total files and total size:
   ```
   find {{params.local-path}} -type f | wc -l
   du -sh {{params.local-path}}
   ```

## Output

Report in this format and store the resolved local path as `build_path`:

```
Build command  : {{params.build-command}}
Output path    : <resolved absolute path>
Total files    : <count>
Total size     : <human-readable size>
Status         : OK
```

Do not modify any files outside of `{{params.local-path}}` in this step.
