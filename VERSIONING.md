# Versioning

EasyCPDLC-Loaded uses plain semantic versioning. Its tags are distinct from upstream
EasyCPDLC tags so releases cannot be confused or collide.

## Release identity

- Git tag: `vMAJOR.MINOR.PATCH`
- Release title: `EasyCPDLC-Loaded MAJOR.MINOR.PATCH`
- Windows assembly / file version: `MAJOR.MINOR.PATCH.0`
- Informational version: `EasyCPDLC-Loaded-vMAJOR.MINOR.PATCH`
- Download: `EasyCPDLC-Loaded-MAJOR.MINOR.PATCH-win-x64.zip`

A pre-release suffix (`-beta`) may be appended to the package name and tag while a
build is being tested. The numeric assembly version never carries the suffix — .NET
requires it to be purely numeric — so it lives on the informational version instead.

## Incrementing

- `PATCH` — compatible fixes and packaging corrections
- `MINOR` — new instrument, network, hardware, printer or loadsheet features
- `MAJOR` — incompatible configuration, protocol or workflow changes

## Building a release

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -Version 1.0.1
```

The script refuses to build unless `-Version` matches `AssemblyFileVersion`, so the
package and the binary can never disagree. Add `-VersionSuffix beta` to label a
pre-release package.

## History

The project was previously released as `EasyCPDLC-Printer-eLC` under
`printer-elc-vMAJOR.MINOR.PATCH` tags, up to `printer-elc-v1.1.0`. It was renamed to
**EasyCPDLC-Loaded** and reset to `1.0.0` when the CDU and GNS430 instruments became
the product. Those older tags are retained for history and are not produced any more.
