# Step: Bump Version in Project Files

Compute the new version and update every project file that declares a version
string. Store the result as `new_version` in the playbook state.

## Compute the new version

1. Find the current version. Check in this priority order:
   a. `Directory.Build.props` — look for `<Version>` element
   b. Any `*.csproj` file — take the first `<Version>` found
   c. If no version element exists, assume `0.1.0`

2. Parse the current version as semver: `MAJOR.MINOR.PATCH`.

3. Apply the bump according to `{{parameters.version-type}}`:

   | version-type | Rule                              |
   |-------------|-----------------------------------|
   | major       | +1 MAJOR, reset MINOR and PATCH   |
   | minor       | +1 MINOR, reset PATCH             |
   | patch       | +1 PATCH                          |

4. The new version string is: `MAJOR.MINOR.PATCH` (no prefix).

## Update project files

For each `.csproj` file found under the workspace root:

- Update `<Version>X.Y.Z</Version>` → `<Version>{{new_version}}</Version>`
- Update `<AssemblyVersion>X.Y.Z.0</AssemblyVersion>` →
  `<AssemblyVersion>{{new_version}}.0</AssemblyVersion>` (if present)
- Update `<FileVersion>X.Y.Z.0</FileVersion>` →
  `<FileVersion>{{new_version}}.0</FileVersion>` (if present)
- Update `<InformationalVersion>` if it exactly matches the old version.

Also update `Directory.Build.props` if it contains a `<Version>` element.

Do NOT update package dependency version references — only the project's own
declared version.

## Stage the changes

After writing all files, stage them with:

```sh
git add **/*.csproj Directory.Build.props
```

## Output

Report in this format:

```
Version bump: <old_version> → <new_version>  ({{parameters.version-type}})

Files updated:
  src/OpenMono.Cli/OpenMono.Cli.csproj
  src/OpenMono.Tests/OpenMono.Tests.csproj
  Directory.Build.props

State stored: new_version = <new_version>
```

## Gate note

This step has a **Confirm** gate. You will display the planned version change
and file list before writing any file.
