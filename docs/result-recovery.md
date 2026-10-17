# Recovering results when the ETABS API read fails

The script now has two result recovery paths. These supplement failed reads for Session 2 engineering checks; they do not manufacture assignments, analysis status, concrete design results, or engineering acceptance criteria. Working API data stays in use.

## Automatic open-table reader

Run the existing report or inspection command while a complete result table is open in ETABS. The script reads its Windows accessibility grid. It does not click, select cases, scroll, save, unlock, run analysis, or edit the model. No Codex desktop tool is needed when the script runs.

The reader requires a uniquely matching ETABS model window, a locked model, all table columns, contiguous rows, and an accessible record count matching the captured row count. It rejects partial/virtualized pages. Force, moment and length headers must expose consistent supported units. It checks the model file before/after reading. Converted cells retain their original display-rounding uncertainty.

Supported table families: Base Reactions, Joint Reactions, Joint Displacements, Story Forces, Story Drifts, Joint Drifts, Modal Periods And Frequencies, Modal Participating Mass Ratios, Mass Summary by Group, and Mass Source Definition. Recovery does not guarantee that every family has sufficient fields for a particular check. Mass summary screen units currently require explicit imported metadata. Only the currently displayed grid is captured; the script does not open hidden tables or switch cases.

The implementation uses Windows Desktop UI Automation; the Windows Desktop .NET 8 runtime is required. It was present on the test machine. Large or inaccessible grids should use file import instead.

## Multiple exported tables

Use a model-bound JSON bundle or CSV manifest:

```powershell
./tools/dotnet.ps1 run --project src/Inspector.Cli -- report --format html --result-tables PATH/manifest.json
./tools/dotnet.ps1 run --project src/Inspector.Cli -- inspect analysis --result-tables PATH/manifest.json
```

The manifest has these fields:

```json
{
  "ModelPath": "C:/absolute/path/Model.EDB",
  "ModelSha256": "SHA256_OF_MODEL_FILE",
  "CapturedAt": "2026-10-16T18:45:00Z",
  "Units": {"Force": "kN", "Length": "m", "Temperature": "C"},
  "Source": "ETABS complete, unfiltered result table exports",
  "Complete": true,
  "Files": [
    {"Key": "Base Reactions", "Path": "base-reactions.csv", "Sha256": "SHA256_OF_CSV"},
    {"Key": "Joint Reactions", "Path": "joint-reactions.csv", "Sha256": "SHA256_OF_CSV"},
    {"Key": "Joint Displacements", "Path": "joint-displacements.csv", "Sha256": "SHA256_OF_CSV"},
    {"Key": "Story Forces", "Path": "story-forces.csv", "Sha256": "SHA256_OF_CSV"},
    {"Key": "Story Drifts", "Path": "story-drifts.csv", "Sha256": "SHA256_OF_CSV"}
  ]
}
```

Export each complete table as one CSV with a single header row. An optional `TABLE: exact table key` first line is supported. Columns use CSI names such as `Output Case`, `Unique Name`, `Step Type`, `Step Number`, `FX`, `FZ`, `Ux`, etc. The loader removes header whitespace. Separate unit rows or mixed-unit headers are deliberately not guessed: supply numeric rows and verified common force/length units in the manifest. Quoted commas and line breaks are handled. Model path/hash, CSV hashes, capture time, completeness declaration, unique table/field names, finite numeric values and supported units are checked. The manifest's claimed model origin/completeness is explicit supplied evidence, not cryptographic proof of ETABS authorship or result freshness. It must describe the actual export.

Alternatively, `Tables` may contain `Key`, `Fields`, and string-valued `Rows` dictionaries directly. A real example is `docs/reports/result-tables-commercial.json`, captured from the open Dead-case Base Reactions grid. That example belongs only to the recorded Commercial model hash. Do not reuse it for another model or revision.

## Calculation scope and precision

- Recovered base reactions can feed gravity balance, reaction totals, reaction-derived mass reconciliation and lateral comparisons when all other necessary inputs exist.
- Recovered joint reactions/displacements, story forces/drifts, modal tables and mass tables can supply the corresponding Session 2 checks. Legacy design/code checks are not silently given rounded replacement datasets.
- Recovery fills unreadable/empty evidence tables; it does not replace successful populated API tables. Original API failures remain traceable.
- Simultaneous steps are inferred only for actual defined linear-static cases explicitly labeled LinStatic. Spectrum and other cases without step metadata remain unspecified and are not summed as simultaneous responses.
- Source rounding is converted with units and propagated into comparisons. Result consistency and independent input-derived comparisons remain different validation scopes.
- The HTML Analysis Results page shows the recovered values, source, time and units. Displayed values do not prove independent structural adequacy.

ETABS does provide self-weight and reactions. The separate independent self-weight/mass calculation currently cannot account for automatic/nonzero member offsets and overlaps. This is now labeled **Inspector limitation**, not missing ETABS self-weight. Full assembled-model eigenanalysis, independent automatic lateral-load generation, and stability/code acceptance without the applicable engineering basis also remain unsupported. Scraping cannot replace those calculations or assumptions.

## Tests and observed live behavior

The standalone script reader successfully captured the complete open Base Reactions table using UI Automation, without result API calls for the grid. Dead FZ was 11,117.664 kN; conversion to the active display units and display precision is tested. The captured case was Dead only. Other table families have parsing/formula tests but have not all been demonstrated against live exported/open grids on this model.

The actual full report command also accepted the captured model-bound bundle and produced `docs/reports/inspection-recovered-dead.html`. It contains the recovered table and no longer reports the Dead reaction as unreadable. Other missing result tables remain unavailable until supplied. The separate independent self-weight limitation remains unverified.

The current automatic test suite has 495 passing tests, with 27 opt-in live tests skipped. Live integration and model protection results are recorded under `trash/test-results/session2-recovery-live.trx` and `session2-recovery-protection.trx`; the standalone screen-reader test was run separately. No model writes or output-selection changes were added.
