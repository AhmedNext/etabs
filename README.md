# ETABS Inspector

A Windows C# inspector for an already-open ETABS v22.7 model. It assesses model state before reporting findings and performs applicable engineering checks without running analysis/design or editing the model.

## Requirements

- Windows with ETABS 22.7 installed and an open model.
- .NET 8 SDK and Windows Desktop runtime. The scripts can use a local SDK under `tools/dotnet` or the SDK on PATH.
- The installed ETABS API assembly. The default installation path is configured in `src/Directory.Build.props`; override `EtabsInstallDir` for another installation directory.

## Run

```powershell
./run.ps1
./run.ps1 inspect analysis
./run.ps1 --help
```

With no arguments, the launcher generates a local HTML report. CSI reference manuals and API help files are included under `tools`. Model files, generated reports, API binaries, bundled SDKs and build artifacts are excluded.

## Test

```powershell
./tools/dotnet.ps1 test src/Inspector.Tests
```

Live tests are opt-in with `ETABS_LIVE_TESTS=1` and require a suitable open ETABS model. See the test-specific requirements before running them.

## Scope and limitations

Findings distinguish model inputs, unreadable API data, incomplete operations and unsupported verification. Analysis results and concrete design results are separate. A numerical PASS applies only to the stated check; it does not certify structural safety or code compliance.

- [Model-state inspection and read-only protections](docs/model-state-inspection.md)
- [Engineering calculations and validation](docs/session2-engineering-validation.md)
- [Result recovery and table import](docs/result-recovery.md)

CSI documentation remains copyrighted by its owner. The ETABS application must be obtained separately. No license for CSI materials is granted by this repository.
