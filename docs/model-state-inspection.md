# Model-aware read-only inspection

The inspector assesses the captured model before running dependent checks. Geometry, connectivity, materials, loads, cases, combinations, mass sources, analysis definitions, and available results continue to be read through the existing extraction path.

- Concrete frame design with no retained results produces one informational finding: **Concrete design has not been run.** Column, beam, detailing, optimization, and design-compatibility checks are deferred with a recorded reason.
- Empty result arrays or a failed API call do not establish that design was never run. Unknown state stays unknown.
- Partial design coverage is grouped by story and ETABS member classification. Each group retains every affected frame ID. Members assigned No Design or another design procedure are excluded. Program-determined members need readable effective material assignments.
- ETABS design errors are grouped separately from missing results. Valid results for other members remain available for checks.
- Analysis cases that have not run are informational. Cases that could not start or finish still report actual analysis failures. Incomplete status inventories cannot establish whole-model completion.
- Missing model inputs, incomplete inspection settings, unavailable API data, optional operations, and engineering review findings have separate finding kinds. Only confirmed project requirements establish that named patterns, combinations, or cases are required.
- Mass-source applicability considers dynamic cases, static acceleration, and actual automatic seismic settings. An empty mass-source table triggers a read of the default-source getter rather than an assumption that no source exists. A getter failure remains unavailable data.
- Repeated state reads detect analysis/design changes during extraction and discard inconsistent snapshots.

The HTML report shows model state, grouped member lists, finding types, and deferred-check reasons. Grouped design failures also retain their frame identities in navigation and floor plans.

## Read-only behavior

Result extraction uses the existing ETABS output selection. It calls selection getters and does not clear or set case/combo selections. Analysis execution is disabled in the command parser, compatibility controller, and ETABS session. Inspection does not run analysis/design, save, unlock, or edit the model.

Result availability means retained results are available. It cannot prove historical execution, freshness, or engineering adequacy. The requested simple “not run” message describes the currently available concrete design state; the underlying availability evidence and this limitation remain in the report.

## CSI references checked

The supplied `tools/CSI API ETABS v1.chm` and `tools/api.chm` have identical SHA-256 hashes. Relevant extracted topics:

| API | CHM topic | Meaning checked |
| --- | --- | --- |
| `cDesignConcrete.GetResultsAvailable()` | `aca6c50f-0931-2828-de22-857763711b04.htm` | Boolean result availability, with no integer return code |
| `cFrameObj.GetDesignProcedure(name, ref int)` | `c3d4f818-61ae-e604-c4e7-decf3fa3a3dc.htm` | 0 program determined, 2 concrete, 7 no design; integer success/failure code |
| `cFrameObj.GetDesignOrientation(name, ref enum)` | `a7f25b8c-377b-3815-e01a-c4995c2ed970.htm` | Actual ETABS member role |
| `cAnalyze.GetCaseStatus(ref count, ref names, ref statuses)` | `94fc4a33-5784-228c-62ad-edcc74eaf034.htm` | Status codes 1–4 and zero success return |
| Output-selection getters | `76ee3052-9f18-fd0a-368c-828ff880318d.htm`, `a079962d-9455-ecc8-6667-f8bc6bf40317.htm` | Ref Boolean selections and integer return codes |
| Column/beam design summaries | `30c9fccf-1a48-4956-c580-3f701e7fecf5.htm`, `0e405de1-f02f-5dbb-6bfc-cc60ab0cea64.htm` | Group retrieval, station arrays, errors/warnings, required-area units; column PMM ratio is check mode and PMM area is design mode |
| `GetAutoSeismicCode` | `fd228654-adaf-a83a-82b0-26a4600799b1.htm` | Call only for Quake patterns; blank means no automatic seismic load |
| `GetMassSource_1` | `3fe46836-82f4-35e8-3eb5-86f2efba259c.htm` | Contribution flags, conditional load arrays, zero success return |

`tools/CSI Analysis Reference Manual.pdf`, sections 17.21–17.21.3 (printed pages 17-11 to 17-13; PDF pages 304–306), distinguishes element mass, additional mass, load-derived mass, intentional deductions, defaults, and case-specific mass sources. Chapter 19 (PDF pages 331–333) explains the mass/stiffness basis of modal analysis and why missing modal operations do not imply model failure.

`tools/ETABS2016-Software-Verification.pdf`, Example 1, printed page 1-1 (PDF page 21), supplies 12×24 inch columns and 12×30 inch beams. Reference tests check gross areas of 288 and 360 in² and their square-meter conversion. These are checks of the inspector's geometry arithmetic using published inputs, not a rerun of the CSI verification solver examples or validation of unrelated code provisions.

API names and signatures were also checked against the installed ETABS v22.7 `ETABSv1.dll`. The connected model was tested through the actual API.

## Verification

Run the automated suite:

```powershell
.\tools\dotnet.ps1 test src/Inspector.sln --no-restore
```

Latest automated result: **454 passed, 23 optional live tests skipped, zero failures**. New scenarios include 1,200 unrun members, 999 partial-design gaps grouped into two stories, failed API reads, design errors across stations, optional assignments, required inputs, state changes, and read-only execution guards.

Targeted live test:

```powershell
$env:ETABS_LIVE_TESTS = '1'
.\tools\dotnet.ps1 test src/Inspector.Tests/Inspector.Tests.csproj --no-build --filter FullyQualifiedName~ReadOnlyModelStateLiveTests
```

It compares the model-file SHA-256, geometry fingerprint, model identity/units/lock, analysis statuses, concrete result availability, and every case/combo output-selection flag before and after inspection. It also compares extracted auto-seismic code values with direct installed-API getters. No analysis or design is started.

The live model reports completed analysis and no concrete frame design results. Its report contains one concrete-design informational message and no per-column or per-beam missing-design findings.
