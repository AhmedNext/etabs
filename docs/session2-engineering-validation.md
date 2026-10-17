# Session 2: engineering calculations and validation

## Update after the user ran analysis

At 21:32 Baghdad time on 2026-10-16, the live read-only rerun confirmed retained analysis results in Commercial_6Story_IraqiCode. All 18 modal period/eigenvalue identities agree. The current output selection does not expose the reaction, joint displacement, and story-force evidence required by the remaining comparisons; these remain explicitly unverified. Independent self-weight/mass also remain limited by automatic/nonzero offsets. Analysis status, units/lock context, output selections, and model-file hash remained unchanged. Both selected live tests passed; the final report-generation rerun also passed. The automated suite still passes 483 tests with 25 opt-in live tests skipped.

The report now has clearer typography and spacing, seven clickable engineering summaries that filter the findings, visible mobile navigation, keyboard focus indicators, sticky table headers, improved print styling, and the actual model name when no project name is configured. Desktop and 390-pixel mobile browser checks confirmed working navigation/filtering and no page overflow. Latest report: `docs/reports/session2-engineering-validation-20261016_183256_859.html`. The no-results final state described below belongs to the preceding run, before the user analyzed the model.

Completed 2026-10-16 against installed ETABS 22.7.0. Session 1 state assessment, grouped design findings, and read-only restrictions are preserved. Analysis and design remain separate. No analysis, design, selection changes, or model writes are performed by the new checks.

## Implemented checks and limits

| Check | Calculation | Validation and remaining limits |
|---|---|---|
| ANA-101 Gravity | Integrate supported uniform area and trapezoidal frame gravity assignments, supported simple self-weight, openings, modifiers, and actual case factors; compare signed vertical reactions. | Independent input-derived comparison. Earlier live observations on Commercial_6Story_IraqiCode: Live 3,762,500 N versus 3,762,499.9999999986 N; SD 6,750,000.0000000009 N versus 6,749,999.9999999823 N. Current reopened model has no retained results, so these comparisons cannot currently be repeated. Automatic/nonzero frame offsets block self-weight validation rather than accepting an approximate overlap correction. |
| ANA-102 Reactions | Sum global joint forces and moments transferred to the actual base reference using M + r cross F. | ETABS result consistency, not independent applied-load equilibrium. Earlier live observations agreed for six components in seven simultaneous static cases. |
| ANA-103 Mass | Actual source flags/factors, supported element mass densities and modifiers, or weighted gravity divided by g; separate reaction-derived source reconciliation. | Independent mass requires supported complete geometry and assignments. Offset-aware self-weight, unsupported additional masses, and negative-load joint clamping block independent validation. Reaction-derived reconciliation is explicitly result consistency only; its new path has automated coverage but no successful live numerical validation in the final no-results state. |
| ANA-104 Drift | Difference between matching upper/lower joint displacements divided by actual story height at reported governing points. | Result consistency. Earlier live observations agreed at 60 governing points across seven cases. Does not certify an independent mesh-wide maximum or a code drift limit. Ground displacement is assumed zero only with verified restraint and absence of applicable ground displacement loads. |
| ANA-105 Lateral | Lowest-story shear magnitude versus base reaction magnitude in simultaneous static cases. | Result consistency only. Earlier live observations agreed for four lateral cases. Independent lateral-load generation and signed-convention validation remain unsupported. Spectrum/envelope extrema are not summed. |
| ANA-106 Modal | Period versus eigenvalue, T = 2 pi / sqrt(lambda); mode validity and positive eigenvalues. | Result consistency. Earlier live observations agreed for 18 modes. Independent full-model modal solution requires assembled stiffness, mass, and constraint mapping; currently unsupported. |
| ANA-107 Stability | theta = P abs(delta) / (abs(V) h), with confirmed gravity basis, actual case lineage, matched simultaneous results and height. | Formula tested independently. Live stability acceptance remains unverified: required paired results/stiffness information and applicable acceptance criteria are not established. No code limit is invented. |

Every calculation checks required inputs and getter success first. Missing essentials produce “Missing input”; unavailable API data and unperformed operations remain distinct. Repeated affected cases are grouped. Passing arithmetic identities are labeled “ETABS result consistency only,” never independent structural adequacy. Comparisons propagate displayed precision and a stated floating-point allowance; neither is a code acceptance tolerance.

## Official references and independent benchmarks

Read the local CSI API CHM and verified the installed signatures/return codes for database display tables, base reactions, joint reactions/drifts, distributed/uniform loads, end offsets, and insertion points. The global Joint Reactions table is used deliberately because the JointReact API reports local components. Actual assignments are read at API precision rather than treating rounded display loads as exact.

The CSI Analysis Reference Manual supplies reaction moment transfer (sections 4.10–4.11), self-weight behavior (7.14), mass-source behavior (17.21–17.21.3), modal mechanics (19.2), and P-delta context (22.3). No unsupported code-compliance claim is derived from these references.

The local ETABS Software Verification PDF supplies:

- Example 1, pages 21–23: independently solved propped cantilever using compatibility and equilibrium. Tests reproduce reactions 67.5 and 112.5 kip and fixed moment -4,860 kip-in, plus section moments.
- Example 2, Table 2-1: equal-story three-mass modal period ratios checked against an independent analytical shear-building solution and the independently expanded characteristic polynomial lambda^3 - 5 lambda^2 + 6 lambda - 1. This validates ratios; it does not claim an independently established absolute frame stiffness or fundamental period.

Additional automated checks cover units including inch/mm, display rounding, nonfinite inputs, polygon geometry, clipped/overlapping openings, moment reference transfer, drift and theta formulas, incomplete inputs, unsupported geometry, failed API reads, signed gravity mismatch, and grouping hundreds of incomplete cases. Separate mass density is never inferred from weight density.

## Final test results and current model

- Automated suite: 483 passed, 25 opt-in live tests skipped, zero failed (508 total). Artifact: `trash/test-results/session2-unit.trx`.
- Selected live suite: two passed, zero failed. Includes the existing Session 1 model-state protection test and the Session 2 engineering/state integration test. Artifact: `trash/test-results/session2-live.trx`.
- Active model: `Commercial_6Story_IraqiCode.EDB`, ETABS 22.7.0. At the final live run, Analyze.GetCaseStatus reported no retained analysis results. All seven dependent calculations were deferred under one informational state finding; no PASS was manufactured. Earlier numerical observations above are historical development observations, not final-run confirmations.
- Before/after checks confirmed identical model identity/units/lock context, analysis statuses, all case/combination output selections, and model-file SHA-256. No changes were made to the open model.
- Final machine-readable evidence: `docs/reports/session2-validation-live.json`. Final HTML: `docs/reports/session2-engineering-validation-20261016_182839_257.html`.

Still unsupported: arbitrary load-assignment types, general opening geometry and offset/intersection-aware self-weight, unexposed additional-mass/joint-clamping behavior, independent automatic lateral-load generation, full assembled-model eigenanalysis, independent mesh-wide drift envelopes, and stability/code certification without complete applicable inputs. Existing standards screens were preserved; these new checks do not establish ASCE/ACI compliance.
