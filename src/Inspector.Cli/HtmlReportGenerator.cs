using System.Globalization;

/// <summary>
/// Emits the self-contained, offline HTML report in the dark "model viewer" design.
/// All report content is rendered server-side (escaped); the inline script only adds navigation,
/// filtering, the 2D-canvas 3D viewer and the finding diagnostics drawer.
/// Geometry is built exclusively from the live ETABS API data already extracted into the model
/// (joints, frames, areas, stories). No .EDB file is decoded.
/// </summary>
public static class HtmlReportGenerator
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static string StateLabel(OperationState state) => state switch
    {
        OperationState.NotRead => "Not read", OperationState.Unknown => "Could not be confirmed",
        OperationState.NotRun => "Not run", OperationState.Partial => "Partly completed",
        OperationState.Failed => "ETABS reports an analysis failure", OperationState.Complete => "Results cover all expected members or cases",
        OperationState.ResultsAvailable => "Results available; full coverage unconfirmed", OperationState.NotApplicable => "Not required for the defined objects",
        _ => "Unknown"
    };
    private static string KindLabel(FindingKind kind) => kind switch
    {
        FindingKind.ModelProblem => "Model problem", FindingKind.MissingInput => "Missing model input",
        FindingKind.IncompleteConfiguration => "Inspection settings need information", FindingKind.ApiUnavailable => "Data could not be read",
        FindingKind.MissingVerificationInput => "Missing verification input",
        FindingKind.OperationNotPerformed => "Operation not completed", FindingKind.ReviewRequired => "Needs review", _ => "Information"
    };

    public static void GenerateReport(InspectionReport report, string path, bool submission = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(Build(report, submission));
    }

    private static string Build(InspectionReport report, bool submission)
    {
        var model = report.Input.Model;
        var identity = model.Identity;
        var units = identity.DisplayUnits;
        var unitText = $"{units.Force} · {units.Length} · {units.Temperature}";
        var fileName = Path.GetFileName(identity.FilePath) is { Length: > 0 } name ? name : "—";
        var project = ProjectName(report.Configuration);
        if (project == "Untitled structural model" && fileName != "—") project = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');
        var findings = report.Findings;
        var counts = findings.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count());
        int Count(CheckStatus s) => counts.TryGetValue(s, out var n) ? n : 0;
        var total = findings.Count;
        var totalChecks = report.Coverage.Count;
        var implemented = report.Coverage.Count(c => c.Implementation == VerificationState.VERIFIED);
        var notImplemented = report.Coverage.Count(c => c.Implementation == VerificationState.NOT_IMPLEMENTED);
        var matrix = Matrix(findings);
        var plans = SafePlans(report);
        var orderedPlans = plans.Reverse().ToList();
        var ran = report.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", Ci);

        var sb = new StringBuilder(1 << 20);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">");
        sb.Append($"<title>{H(project)} — Structural Engineering Review</title><style>");
        sb.Append(Css);
        sb.Append(ReportPolishCss);
        if (submission) sb.Append("\n@page{size:landscape;margin:12mm}");
        sb.Append("</style></head><body>");

        if (submission) sb.Append(SubmissionSheet(project, fileName, identity.EtabsVersion, ran, matrix));

        sb.Append("<div class=\"app\"><aside><div class=\"burger\">");
        sb.Append("<svg class=\"i\" viewBox=\"0 0 24 24\" style=\"width:24px;height:24px\"><path d=\"M4 7h16M4 12h16M4 17h16\"/></svg></div>");
        sb.Append("<nav class=\"side\" id=\"nav\">");
        foreach (var (page, anchor, label, path) in NavItems)
            sb.Append($"<a href=\"#{H(anchor ?? page)}\" data-go=\"{page}\"{(anchor is null ? "" : $" data-anchor=\"{anchor}\"")}>{Ico(path)}{H(label)}</a>");
        sb.Append("</nav><nav class=\"side bot\"><a id=\"print\">");
        sb.Append("<svg class=\"i\" viewBox=\"0 0 24 24\"><path d=\"M7 9V3h10v6M7 17H4v-7h16v7h-3M7 14h10v7H7z\"/></svg>Print / Save PDF</a></nav></aside>");

        sb.Append("<div><header class=\"top\"><div>");
        sb.Append($"<h1 id=\"h-proj\">{H(project)}</h1><p>Structural Inspection &nbsp;|&nbsp; ETABS Model (Read-Only, Live API)</p></div><span class=\"sp\"></span>");
        sb.Append("<div class=\"meta\" id=\"h-meta\">");
        sb.Append($"<span>Model:<b>{H(fileName)}</b></span><span>ETABS:<b>{H(identity.EtabsVersion)}</b></span>");
        sb.Append($"<span>Units:<b>{H(unitText)}</b></span><span>Code:<b>ASCE 7-16 · ACI 318-19</b></span><span>Run:<b>{H(ran)}</b></span>");
        sb.Append($"<span>Findings:<b>{total} findings overall</b></span>");
        sb.Append("</div></header><main id=\"main-content\">");

        // ---- viewer page ----
        sb.Append("<section class=\"page\" data-page=\"viewer\">");
        sb.Append("<div class=\"review-intro\"><div><span class=\"eyebrow\">MODEL REVIEW · READ ONLY</span><h2>Your model, at a glance.</h2><p>Start with the items that need attention, then explore the calculation evidence.</p></div><a class=\"primary-action\" data-go=\"findings\" href=\"#sec-findings\">Review findings <span aria-hidden=\"true\">→</span></a></div>");
        sb.Append("<div class=\"kpis\">");
        sb.Append("<div class=\"card kpi\"><div class=\"ic\" style=\"background:rgba(34,211,238,.12);color:var(--accent)\">");
        sb.Append(Ico("M7 3h7l5 5v13H7zM14 3v5h5M10 13h6M10 17h6", "width:24px;height:24px"));
        sb.Append($"</div><div><span>Total checks</span><strong>{totalChecks}</strong>");
        sb.Append($"<div class=\"bd\"><span>Findings<b> {total}</b></span><span>Pass<b> {Count(CheckStatus.PASS)}</b></span><span>Warn<b> {Count(CheckStatus.WARNING)}</b></span><span>Not verified<b> {Count(CheckStatus.NOT_VERIFIED)}</b></span><span>Skipped<b> {Count(CheckStatus.SKIPPED)}</b></span><span>N/A<b> {Count(CheckStatus.NOT_APPLICABLE)}</b></span></div></div></div>");
        sb.Append("<div class=\"card kpi\"><div class=\"ic\" style=\"background:rgba(244,63,94,.85);color:#fff\">");
        sb.Append(Ico("M12 3 2 20h20L12 3zM12 10v4M12 17h.01", "width:24px;height:24px"));
        sb.Append($"</div><div><span>Failures</span><strong style=\"color:#fb7185\">{Count(CheckStatus.FAIL)}</strong><small>{Share(Count(CheckStatus.FAIL), total)} of findings</small></div></div>");
        sb.Append("<div class=\"card kpi\">");
        sb.Append(Ring(ShareValue(Count(CheckStatus.WARNING), total), "var(--warn)", Count(CheckStatus.WARNING)));
        sb.Append($"<div><span>Warnings</span><div class=\"lvl\" style=\"color:var(--warn)\">{Share(Count(CheckStatus.WARNING), total)}</div><small>of findings need review</small><small>{Count(CheckStatus.NOT_VERIFIED)} more not verified</small></div></div>");
        sb.Append("<div class=\"card kpi\">");
        sb.Append(Ring(ShareValue(implemented, totalChecks), "var(--accent)", 0));
        sb.Append($"<div><span>Catalog coverage</span><div class=\"lvl\" style=\"color:var(--accent)\">{implemented} / {totalChecks} checks</div><small>verified rules in the catalog</small><small>{notImplemented} checks not implemented</small></div></div>");
        sb.Append("<div class=\"card kpi\">");
        sb.Append(Ring(ShareValue(Count(CheckStatus.PASS), total), "var(--pass)", 0));
        sb.Append($"<div><span>Passed</span><div class=\"lvl\" style=\"color:#4ade80\">{Count(CheckStatus.PASS)} findings</div><small>{Share(Count(CheckStatus.PASS), total)} of {total} findings</small><small>Gray in the model ≠ verified safe</small></div></div>");
        sb.Append("</div>");

        var modelState = report.ModelState;
        sb.Append($"<div class=\"panel-note\"><b>Model state</b> · Analysis: {H(StateLabel(modelState.Analysis))} · Concrete design: {H(StateLabel(modelState.ConcreteDesign))}. Checks use the available data and the stated project requirements.</div>");
        var engineering = findings.Where(f => f.CheckId.StartsWith("ANA-10", StringComparison.Ordinal)).ToArray();
        if (engineering.Length > 0)
            sb.Append($"<div class=\"panel-note\"><b>Engineering verification</b> · {engineering.Count(f => f.CalculationValidation == CalculationValidation.IndependentLoadBalance)} input-derived balances · {engineering.Count(f => f.CalculationValidation == CalculationValidation.ResultConsistency)} result-consistency comparisons · {engineering.Count(f => f.Status == CheckStatus.NOT_VERIFIED)} unverified findings. Result consistency does not establish independent structural adequacy.</div>");
        if (engineering.Length > 0)
        {
            sb.Append("<div class=\"verification-overview\" aria-label=\"Engineering verification overview\">");
            foreach (var group in engineering.GroupBy(f => f.CheckId))
            {
                var attention = group.Count(f => f.Status is CheckStatus.FAIL or CheckStatus.WARNING or CheckStatus.ERROR);
                var verified = group.Count(f => f.CalculationValidation != CalculationValidation.Unverified);
                var names = new Dictionary<string,string> { ["ANA-101"]="Gravity balance", ["ANA-102"]="Support reactions", ["ANA-103"]="Building mass", ["ANA-104"]="Story drift", ["ANA-105"]="Lateral behavior", ["ANA-106"]="Modal behavior", ["ANA-107"]="Stability" };
                sb.Append($"<a class=\"verification-tile\" data-go=\"findings\" data-check=\"{H(group.Key)}\" href=\"#sec-findings\"><span class=\"eyebrow\">{H(group.Key)}</span><strong>{H(names.GetValueOrDefault(group.Key,group.Key))}</strong><span>{(attention > 0 ? $"{attention} need review" : verified > 0 ? $"{verified} comparisons agree" : "Missing input / unverified")}</span><small>{group.Count(f => f.Status == CheckStatus.NOT_VERIFIED)} unverified · View evidence →</small></a>");
            }
            sb.Append("</div>");
        }

        sb.Append("<div class=\"grid\"><div class=\"card\"><div class=\"vh\">");
        sb.Append("<div class=\"seg\" id=\"cams\"><button class=\"on\" data-cam=\"iso\">3D View</button><button data-cam=\"top\">Plan View</button><button data-cam=\"front\">Elevation</button></div>");
        sb.Append("<select class=\"sel\" id=\"story\" aria-label=\"Story\"></select><span class=\"sp\"></span>");
        sb.Append("<button class=\"ib on\" id=\"tco\" title=\"Show callouts\"><svg class=\"i\" viewBox=\"0 0 24 24\" style=\"width:16px;height:16px\"><path d=\"M4 5h16v10H9l-5 4z\"/></svg></button>");
        sb.Append("<button class=\"ib\" data-zoom=\"1.25\" title=\"Zoom in\"><svg class=\"i\" viewBox=\"0 0 24 24\" style=\"width:16px;height:16px\"><circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-4-4M8 11h6M11 8v6\"/></svg></button>");
        sb.Append("<button class=\"ib\" data-zoom=\"0.8\" title=\"Zoom out\"><svg class=\"i\" viewBox=\"0 0 24 24\" style=\"width:16px;height:16px\"><circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-4-4M8 11h6\"/></svg></button>");
        sb.Append("<button class=\"ib\" id=\"reset\" title=\"Reset view\"><svg class=\"i\" viewBox=\"0 0 24 24\" style=\"width:16px;height:16px\"><path d=\"M4 9V4h5M20 15v5h-5M4 4l6 6M20 20l-6-6\"/></svg></button></div>");
        sb.Append("<div class=\"vbody\"><div class=\"stage\" id=\"stage\"><canvas id=\"c3\"></canvas><svg class=\"lead\" id=\"lead\"></svg><div id=\"cos\"></div><div class=\"gz\">Drag to rotate · scroll to zoom · right-drag to pan</div></div>");
        sb.Append("<div class=\"side3\"><div><h4>Layers</h4><div id=\"layers\"></div></div><div><h4>Result Display</h4><div class=\"sel\" style=\"min-width:0\">Check status</div></div>");
        sb.Append("<div class=\"lgd\"><div><i style=\"background:#8ea3bd\"></i>Pass / no finding</div><div><i style=\"background:var(--warn)\"></i>Warning</div><div><i style=\"background:var(--fail)\"></i>Fail</div><div><i style=\"background:var(--accent)\"></i>Selected</div></div></div>");
        sb.Append("</div></div><div class=\"card dg\" id=\"dg\"></div></div>");

        sb.Append("<div class=\"r3\">");
        sb.Append("<div class=\"card bc\"><h3>Compliance Matrix <a data-go=\"signoff\">Sign-off</a></h3><div class=\"cm\" id=\"cm\">");
        foreach (var (label, status, basis) in matrix)
            sb.Append($"<div><span>{H(label)}</span>{Badge(status)}<small title=\"{H(basis)}\">{H(basis)}</small></div>");
        sb.Append("</div></div>");
        sb.Append("<div class=\"card bc\"><h3>Modal Participation <a data-go=\"results\" data-anchor=\"sec-modal\">Table</a></h3>");
        sb.Append($"<table class=\"mt\" id=\"modes\">{ModalMini(report)}</table><div class=\"ms\" id=\"msum\">{ModalSummary(report)}</div></div>");
        sb.Append("<div class=\"card bc\"><h3>Drift Check (amplified Δ/h) <a data-go=\"results\" data-anchor=\"sec-drift\">Table</a></h3>");
        sb.Append($"<div class=\"dl\"><span style=\"--c:#22d3ee\">X-dir</span><span style=\"--c:#f59e0b\">Y-dir</span><span style=\"--c:var(--fail)\">Limit {H(LimitText(report))}</span></div>");
        sb.Append($"<div class=\"dr\" id=\"drift\">{DriftBars(report)}</div></div>");
        sb.Append("<div class=\"card bc\"><h3>Recommendations <a data-go=\"findings\">All findings</a></h3><ol class=\"rl\" id=\"recs\">");
        foreach (var rec in Recommendations(findings))
            sb.Append($"<li><div style=\"min-width:0\"><b>{H(rec.Text)}</b><small>{H(rec.CheckId)} · {H(rec.Element ?? "model")}{(rec.Story is null ? "" : " · " + H(rec.Story))} — {H(rec.Title)}</small></div><span class=\"sv {rec.SeverityClass}\">{H(SeverityText(rec.Severity))}</span></li>");
        sb.Append("</ol></div></div></section>");

        // ---- secondary pages ----
        sb.Append("<section class=\"page\" data-page=\"findings\" hidden>").Append(FindingsPanel(findings, total)).Append("</section>");
        sb.Append("<section class=\"page\" data-page=\"results\" hidden>").Append(RecoveredTablesPanel(report)).Append(ResultsPanels(report)).Append("</section>");
        sb.Append("<section class=\"page\" data-page=\"plans\" hidden>").Append(PlansPanel(orderedPlans, units.Length)).Append("</section>");
        sb.Append("<section class=\"page\" data-page=\"quantities\" hidden>").Append(QuantitiesPanel(report)).Append("</section>");
        sb.Append("<section class=\"page\" data-page=\"evidence\" hidden>").Append(EvidencePanel(report, total)).Append("</section>");
        sb.Append("<section class=\"page\" data-page=\"signoff\" hidden>").Append(SignoffPanel(project, fileName, identity.EtabsVersion, ran, matrix)).Append("</section>");

        sb.Append("</main></div></div>");

        sb.Append("<script>");
        sb.Append($"window.GEOMETRY={GeometryJson(report)};");
        sb.Append($"window.FINDINGS={FindingsJson(report)};");
        sb.Append($"window.MODEL_META={MetaJson(project, identity, unitText, ran, total, totalChecks, implemented, notImplemented)};");
        sb.Append(Js);
        sb.Append("</script></body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ panels
    private static string RecoveredTablesPanel(InspectionReport report)
    {
        var tables=report.Input.Analysis?.Results?.VerificationEvidence?.Tables.Where(t=>t.RecoverySource is not null).ToArray()??[];
        if(tables.Length==0)return "";
        var sb=new StringBuilder("<div class=\"panel\"><div class=\"panel-h\"><div><h2>Recovered ETABS results</h2><p class=\"sub\">The API read failed. These values were recovered from a complete open table or an explicitly imported table bundle. Display rounding is retained; this is not independent validation.</p></div></div>");
        sb.Append($"<p class=\"panel-note\">Values converted to report units: {H(report.Input.Model.Identity.DisplayUnits.Force)} · {H(report.Input.Model.Identity.DisplayUnits.Length)} · seconds. Mass uses force·s²/length.</p>");
        foreach(var table in tables)
        {
            sb.Append($"<details class=\"inventory\" open><summary>{H(table.Key)} · {table.Rows.Count} rows</summary><p class=\"sub\">{H(table.RecoverySource)} · {H(table.CapturedAt?.ToString("u",Ci)??"Capture time unknown")}</p><div class=\"table-wrap\"><table><thead><tr>");
            foreach(var field in table.Fields)sb.Append($"<th>{H(field)}</th>");
            sb.Append("</tr></thead><tbody>");
            foreach(var row in table.Rows){sb.Append("<tr>");foreach(var field in table.Fields)sb.Append($"<td>{H(row.GetValueOrDefault(field,"—"))}</td>");sb.Append("</tr>");}
            sb.Append("</tbody></table></div></details>");
        }
        return sb.Append("</div>").ToString();
    }

    private static string FindingsPanel(IReadOnlyList<Finding> findings, int total)
    {
        var statuses = new[] { CheckStatus.FAIL, CheckStatus.WARNING, CheckStatus.NOT_VERIFIED, CheckStatus.PASS, CheckStatus.SKIPPED, CheckStatus.NOT_APPLICABLE, CheckStatus.ERROR };
        var modules = findings.Select(f => ModuleKey(f.Module)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("<div class=\"panel\" id=\"sec-findings\"><div class=\"panel-h\"><div><h2>Complete findings explorer</h2><p class=\"sub\">Every finding from the live ETABS model, with the calculation and the supporting evidence behind it.</p></div>");
        sb.Append("<div class=\"controls\"><label>Status<select id=\"status-filter\">");
        sb.Append("<option value=\"ALL\">All statuses</option>");
        foreach (var s in statuses) sb.Append($"<option value=\"{s}\">{H(Label(s))}</option>");
        sb.Append("</select></label><label>Module<select id=\"module-filter\"><option value=\"ALL\">All modules</option>");
        foreach (var m in modules) sb.Append($"<option value=\"{H(m)}\">{H(m)}</option>");
        sb.Append("</select></label><label>Search<input id=\"finding-search\" type=\"search\" placeholder=\"Element, story, check ID or description\"></label></div></div>");
        sb.Append($"<p class=\"sub pad\"><b data-f=\"count\">{total}</b> findings shown · Expand a description for calculations and evidence</p>");
        sb.Append("<div class=\"table-wrap\"><table class=\"findings\"><thead><tr><th>Check / module</th><th>Status</th><th>Element / story</th><th>Description &amp; evidence</th><th>Actual</th><th>Limit</th></tr></thead><tbody>");
        foreach (var f in findings)
        {
            var search = string.Join(' ', new[] { f.CheckId, f.Module, f.ElementId, f.Story, f.Title, string.Join(" ", f.AffectedElements) }.Where(x => !string.IsNullOrWhiteSpace(x)));
            sb.Append($"<tr data-status=\"{f.Status}\" data-module=\"{H(ModuleKey(f.Module))}\" data-search=\"{H(search.ToLowerInvariant())}\">");
            sb.Append($"<td class=\"check\">{H(f.CheckId)}<small>{H(f.Module)}</small></td>");
            sb.Append($"<td>{Badge(f.Status)}</td>");
            sb.Append($"<td>{H(f.ElementId ?? "—")}<small>{H(f.Story ?? string.Empty)}</small></td>");
            sb.Append($"<td class=\"desc\"><details class=\"finding-detail\"><summary>{H(f.Title)}</summary>{Drawer(f)}</details></td>");
            sb.Append($"<td>{Num(f.Actual)}</td><td>{Num(f.Limit)}</td></tr>");
        }
        sb.Append("</tbody></table></div><p class=\"empty\" data-f=\"empty\" hidden>No findings match the selected filters.</p></div>");
        return sb.ToString();
    }

    private static string Drawer(Finding f)
    {
        var sb = new StringBuilder("<div class=\"drawer\"><dl class=\"metrics\">");
        sb.Append($"<div><dt>Check</dt><dd>{H(f.CheckId)} · {H(f.Module)}</dd></div>");
        sb.Append($"<div><dt>Status</dt><dd>{H(Label(f.Status))}</dd></div>");
        sb.Append($"<div><dt>Finding type</dt><dd>{H(KindLabel(f.Kind))}</dd></div>");
        sb.Append($"<div><dt>Engineering calculation validation</dt><dd>{H(f.CalculationValidation switch { CalculationValidation.IndependentLoadBalance => "Input-derived load balance", CalculationValidation.ResultConsistency => "ETABS result consistency only", _ => "Not independently validated" })}</dd></div>");
        sb.Append($"<div><dt>Actual</dt><dd>{Num(f.Actual)}</dd></div>");
        sb.Append($"<div><dt>Limit</dt><dd>{Num(f.Limit)}</dd></div>");
        sb.Append($"<div><dt>Element</dt><dd>{H(f.ElementId ?? "—")}</dd></div>");
        sb.Append($"<div><dt>Story</dt><dd>{H(f.Story ?? "—")}</dd></div>");
        sb.Append($"<div><dt>Load case</dt><dd>{H(f.LoadCaseOrCombination ?? "Not specified")}</dd></div>");
        sb.Append($"<div><dt>Standard &amp; clause</dt><dd>{H(f.Standard ?? "Not specified")} · {H(f.CodeClause ?? "Not specified")}</dd></div>");
        sb.Append($"<div><dt>Severity</dt><dd>{H(f.Severity.ToString())}</dd></div>");
        sb.Append($"<div><dt>Rule version</dt><dd>{H(f.RuleVersion)}</dd></div>");
        sb.Append("</dl>");
        if (f.AffectedElements.Count > 0)
            sb.Append($"<h4>Affected members ({f.AffectedElements.Count})</h4><p class=\"txt\">{H(string.Join(", ", f.AffectedElements))}</p>");
        if (f.RelatedCheckIds.Count > 0)
            sb.Append($"<h4>Checks covered by this message</h4><p class=\"txt\">{H(string.Join(", ", f.RelatedCheckIds))}</p>");
        sb.Append("<h4>Engineering diagnosis</h4>");
        if (f.PossibleCauses.Count == 0) sb.Append("<p class=\"txt muted\">No diagnosis supplied.</p>");
        else { sb.Append("<ul class=\"ul\">"); foreach (var c in f.PossibleCauses) sb.Append($"<li>{H(c)}</li>"); sb.Append("</ul>"); }
        sb.Append("<h4>Recommendations</h4>");
        if (f.Recommendations.Count == 0) sb.Append("<p class=\"txt muted\">No recommendation supplied.</p>");
        else { sb.Append("<ul class=\"ul\">"); foreach (var r in f.Recommendations) sb.Append($"<li>{H(r)}</li>"); sb.Append("</ul>"); }
        sb.Append($"<h4>Calculation method &amp; formula</h4><p class=\"txt\">{H(f.CalculationMethod)}</p>");
        sb.Append($"<h4>Verification procedure</h4><p class=\"txt\">{H(f.VerificationProcedure)}</p>");
        sb.Append("<h4>Supporting evidence (ETABS)</h4>");
        if (f.Evidence.Count == 0) sb.Append("<p class=\"txt muted\">No supporting evidence supplied.</p>");
        else
        {
            sb.Append("<table class=\"cmp\"><thead><tr><th>Parameter</th><th>ETABS source (API / table)</th><th>Value</th></tr></thead><tbody>");
            foreach (var e in f.Evidence)
                sb.Append($"<tr><td>{H(e.Description)}</td><td><small>{H(e.Source)}</small></td><td><code>{H(e.Value)}{(e.Unit is null ? string.Empty : " " + H(e.Unit))}</code></td></tr>");
            sb.Append("</tbody></table>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string ResultsPanels(InspectionReport report)
    {
        var sb = new StringBuilder();
        var analysis = report.Input.Analysis;
        var results = analysis?.Results;

        // modal
        sb.Append("<section class=\"panel\" id=\"sec-modal\"><div class=\"panel-h\"><div><h2>Modal mass participation</h2>");
        sb.Append($"<p class=\"sub\">Results.ModalParticipatingMassRatios · cumulative modal mass; the {TargetText(report)} screening target uses the reported cumulative sums.</p></div></div>");
        var modal = results?.ModalParticipation?.Items;
        if (modal is null || modal.Count == 0)
            sb.Append("<p class=\"empty\">Modal participation results unavailable.</p>");
        else
        {
            sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Case</th><th>Step</th><th>Mode</th><th>T (s)</th><th>Frequency (Hz)</th><th>UX</th><th>UY</th><th>UZ</th><th>ΣUX</th><th>ΣUY</th><th>ΣUZ</th><th>RZ</th><th>ΣRZ</th><th>90% UX &amp; UY</th></tr></thead><tbody>");
            var target = Target(report);
            foreach (var m in modal)
            {
                var ok = m.SumUX >= target && m.SumUY >= target;
                sb.Append($"<tr><td>{H(m.Case)}</td><td>{H(m.StepType)}</td><td>{Num(StepDisplay(m, results))}</td><td>{Num(m.Period)}</td><td>{Num(SafeInverse(m.Period))}</td>");
                sb.Append($"<td>{Pct(m.UX)}</td><td>{Pct(m.UY)}</td><td>{Pct(m.UZ)}</td><td>{Pct(m.SumUX)}</td><td>{Pct(m.SumUY)}</td><td>{Pct(m.SumUZ)}</td><td>{Pct(m.RZ)}</td><td>{Pct(m.SumRZ)}</td>");
                sb.Append($"<td>{Badge(ok ? CheckStatus.PASS : CheckStatus.WARNING)}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
            sb.Append("<details class=\"inventory\"><summary>Exact modal-period source table</summary>");
            sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Case</th><th>Mode</th><th>Reported period (s)</th><th>Reported frequency (Hz)</th></tr></thead><tbody>");
            foreach (var m in modal)
                sb.Append($"<tr><td>{H(m.Case)}</td><td>{Num(StepDisplay(m, results))}</td><td>{Num(m.Period)}</td><td>{Num(SafeInverse(m.Period))}</td></tr>");
            sb.Append("</tbody></table></div></details>");
            sb.Append("<p class=\"panel-note\">Cumulative modal mass below the screening target is reported, never inferred: unresolved steps are matched to the unique period/frequency evidence in the ETABS result tables, otherwise they stay “—”.</p>");
        }
        sb.Append("</section>");

        // base reactions
        sb.Append("<section class=\"panel\" id=\"sec-base\"><div class=\"panel-h\"><div><h2>Base reactions &amp; shear scaling</h2>");
        sb.Append("<p class=\"sub\">Results.BaseReact · signed base reactions per case and direction. Weight W and Cs are read from the mapped ETABS auto-seismic pattern; W is never inferred from the vertical base reaction.</p></div></div>");
        var baseReactions = results?.BaseReactions?.Items;
        if (baseReactions is null || baseReactions.Count == 0)
            sb.Append("<p class=\"empty\">Base reaction results unavailable.</p>");
        else
        {
            sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Case</th><th>Step</th><th>Direction</th><th>Signed reaction</th><th>Force |V|</th><th>Weight W and Cs</th><th>Cs</th><th>Generated V</th><th>Magnitude residual</th></tr></thead><tbody>");
            var auto = AutoSeismic(report);
            foreach (var r in baseReactions)
            {
                var pattern = MappedPattern(report, r.Case);
                var direction = MappedDirection(report, r.Case);
                foreach (var (dir, signed) in new[] { ("X", r.FX), ("Y", r.FY), ("Z", r.FZ) })
                {
                    var mapped = pattern is not null && direction == dir;
                    var magnitude = Math.Abs(signed);
                    var generated = mapped ? pattern!.Value.BaseShear : (double?)null;
                    var weight = mapped ? pattern!.Value.WeightUsed : (double?)null;
                    var coeff = mapped ? pattern!.Value.CoeffUsed : (double?)null;
                    var residual = generated is { } g ? Math.Abs(magnitude - Math.Abs(g)) : (double?)null;
                    sb.Append($"<tr><td>{H(r.Case)}</td><td>{H(r.StepType)} / {Num(r.StepNumber)}</td><td>{H(dir)}</td><td>{Num(signed)}</td><td>{Num(magnitude)}</td>");
                    sb.Append($"<td>{Num(weight)}</td><td>{Num(coeff)}</td><td>{Num(generated)}</td><td>{Num(residual)}</td></tr>");
                }
            }
            sb.Append("</tbody></table></div>");
            sb.Append("<p class=\"panel-note\">Magnitude residual = ||reaction| − |generated V|| for a unique mapped static case. A residual is shown only when a single auto-seismic pattern maps to the case and its axis is configured.</p>");
        }
        sb.Append("</section>");

        // drift
        sb.Append("<section class=\"panel\" id=\"sec-drift\"><div class=\"panel-h\"><div><h2>Story drift &amp; stability</h2>");
        sb.Append("<p class=\"sub\">Results.StoryDrifts · amplified drift Δ/h = Cd·δe/(Ie·h). θ and θ max come from the SEIS-017 stability findings; the P-Delta configuration is read from the ETABS analysis definitions.</p></div></div>");
        var drifts = results?.StoryDrifts?.Items;
        if (drifts is null || drifts.Count == 0)
            sb.Append("<p class=\"empty\">Story drift results unavailable.</p>");
        else
        {
            sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Story</th><th>Case</th><th>Direction</th><th>Elastic Δ/h</th><th>Amplified Δ/h</th><th>Project limit</th><th>θ</th><th>θ max</th><th>P-Delta configuration</th><th>Drift</th><th>Stability</th></tr></thead><tbody>");
            var seismic = report.Configuration.Settings.SeismicVerification;
            foreach (var d in drifts)
            {
                var amplified = d.Drift * (seismic is null || seismic.Ie == 0 ? 1 : seismic.Cd / seismic.Ie);
                var stability = StabilityFinding(report, d.Story, d.Case, d.Direction);
                var driftFinding = DriftFinding(report, d.Story, d.Case, d.Direction);
                sb.Append($"<tr><td>{H(d.Story)}</td><td>{H(d.Case)}</td><td>{H(d.Direction)}</td>");
                sb.Append($"<td>{Num(d.Drift)}</td><td>{Num(amplified)}</td><td>{Num(seismic?.DriftLimit)}</td>");
                sb.Append($"<td>{Num(stability?.Actual)}</td><td>{Num(stability?.Limit)}</td><td>{H(PDeltaText(report, d.Case))}</td>");
                sb.Append($"<td>{(driftFinding is null ? "—" : Badge(driftFinding.Status))}</td>");
                sb.Append($"<td>{(stability is null ? "—" : Badge(stability.Status))}</td></tr>");
            }
            sb.Append("</tbody></table></div>");
            sb.Append("<p class=\"panel-note\">The amplified drift uses the configured Cd/Ie. PASS/FAIL badges appear only where a matching SEIS-006 / SEIS-017 finding exists; otherwise the cell stays “—”.</p>");
        }
        sb.Append("</section>");
        return sb.ToString();
    }

    private static string PlansPanel(IReadOnlyList<SvgPlanGenerator.FloorPlan> plans, string lengthUnit)
    {
        var sb = new StringBuilder("<div class=\"panel\" id=\"sec-plans\"><div class=\"panel-h\"><div><h2>Structural floor plans</h2>");
        sb.Append("<p class=\"sub\">Plans are embedded inline from the live ETABS geometry. Red FAIL, orange WARNING, gray PASS / other / no finding; gray does not establish safety.</p></div>");
        sb.Append("<div class=\"controls\"><label>Story<select id=\"story-selector\">");
        for (var i = 0; i < plans.Count; i++) sb.Append($"<option value=\"{i}\">{H(plans[i].Story)}</option>");
        sb.Append("</select></label></div></div>");
        sb.Append("<div class=\"legend\"><span style=\"--c:var(--fail)\">FAIL</span><span style=\"--c:var(--warn)\">WARNING</span><span style=\"--c:#d1d5db\">PASS / other / no finding</span></div>");
        sb.Append("<div class=\"plan-stage\">");
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            sb.Append($"<div class=\"floor-plan\" data-story-plan=\"{i}\"{(i > 0 ? " hidden" : string.Empty)}>");
            sb.Append($"<h3>{H(plan.Story)} · elevation {Num(plan.Elevation)} {H(lengthUnit)}</h3>");
            sb.Append(UniqueIds(plan.SvgMarkup, i));
            sb.Append("</div>");
        }
        if (plans.Count == 0) sb.Append("<div class=\"floor-plan\" data-story-plan=\"0\"><h3>No stories available</h3><p class=\"empty\">The live ETABS model returned no story data.</p></div>");
        sb.Append("</div><p class=\"panel-note\">Only direct frame/area/joint findings and joint pairs are mapped to objects; story, section and pier findings are not drawn.</p></div>");
        return sb.ToString();
    }

    private static string QuantitiesPanel(InspectionReport report)
    {
        var sb = new StringBuilder("<div class=\"panel\" id=\"sec-qty\"><div class=\"panel-h\"><div><h2>Material quantities &amp; rebar estimation</h2>");
        sb.Append("<p class=\"sub\">Concrete volumes come from the ETABS material takeoff; steel is estimated with low / base / high reinforcement rates. Estimates are not a design.</p></div></div>");
        var estimate = report.Input.RebarEstimate;
        if (estimate is null || estimate.Items.Count == 0)
        {
            sb.Append("<p class=\"empty\">Rebar estimation unavailable; usable concrete volumes were not returned by the live ETABS model.</p>");
        }
        else
        {
            sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Object type</th><th>Material</th><th>Concrete (m³)</th><th>Low rate (kg/m³)</th><th>Base rate (kg/m³)</th><th>High rate (kg/m³)</th><th>Low (t)</th><th>Base (t)</th><th>High (t)</th></tr></thead><tbody>");
            foreach (var row in estimate.Items)
            {
                sb.Append($"<tr><td>{H(row.ObjectType)}</td><td>{H(row.Material)}</td><td>{Qty(row.ConcreteVolumeM3)}</td>");
                sb.Append($"<td>{Qty(row.Rates.LowKgPerM3)}</td><td>{Qty(row.Rates.BaseKgPerM3)}</td><td>{Qty(row.Rates.HighKgPerM3)}</td>");
                sb.Append($"<td>{Qty(row.LowSteelKg / 1000)}</td><td>{Qty(row.BaseSteelKg / 1000)}</td><td>{Qty(row.HighSteelKg / 1000)}</td></tr>");
            }
            sb.Append($"<tr><td>Total</td><td>—</td><td>{Qty(estimate.TotalConcreteM3)}</td><td>—</td><td>—</td><td>—</td>");
            sb.Append($"<td>{Qty(estimate.LowSteelKg / 1000)}</td><td>{Qty(estimate.BaseSteelKg / 1000)}</td><td>{Qty(estimate.HighSteelKg / 1000)}</td></tr>");
            sb.Append("</tbody></table></div>");
        }
        sb.Append("<details class=\"inventory\"><summary>Estimating assumptions &amp; limitations</summary><ul class=\"ul\">");
        if (estimate is null || estimate.Limitations.Count == 0) sb.Append("<li>No limitation was reported.</li>");
        else foreach (var l in estimate.Limitations) sb.Append($"<li>{H(l)}</li>");
        sb.Append("</ul></details></div>");
        return sb.ToString();
    }

    private static string EvidencePanel(InspectionReport report, int total)
    {
        var sb = new StringBuilder("<div class=\"panel\" id=\"sec-evidence\"><div class=\"panel-h\"><div><h2>Analysis evidence &amp; coverage</h2>");
        sb.Append("<p class=\"sub\">Every value in this report is traceable to a live ETABS API read or an ETABS result table. Nothing is decoded from the .EDB file.</p></div></div>");

        IReadOnlyList<AnalysisEvidenceTable> tables = (report.Input.Analysis?.Results?.Extended?.Tables ?? [])
            .Concat(report.Input.Analysis?.Results?.VerificationEvidence?.Tables ?? []).DistinctBy(t => t.Key).ToArray();
        sb.Append("<details class=\"inventory\" open><summary>ETABS database tables read (").Append(tables.Count).Append(")</summary><ul class=\"ul\">");
        if (tables.Count == 0) sb.Append("<li>No extended analysis tables were returned.</li>");
        foreach (var t in tables)
            sb.Append($"<li><strong>{H(t.Key)}</strong> — {t.Rows.Count} rows<small>{H(string.Join(", ", t.Fields))}</small></li>");
        sb.Append("</ul></details>");

        var snapshots = report.Input.Analysis?.Results?.ResultTables ?? [];
        sb.Append("<details class=\"inventory\"><summary>Result tables (E.SelectedResults) (").Append(snapshots.Count).Append(")</summary><ul class=\"ul\">");
        if (snapshots.Count == 0) sb.Append("<li>No result-table snapshots were returned.</li>");
        foreach (var t in snapshots)
            sb.Append($"<li><strong>{H(t.Name)}</strong> — {t.Rows} rows{(t.IsEmpty ? " (empty)" : string.Empty)}<small>{H(string.Join(", ", t.Fields))}</small></li>");
        sb.Append("</ul></details>");

        var issues = new List<string>();
        foreach (var i in report.Input.Model.Issues) issues.Add($"[model · {i.Component}] {i.Message}");
        foreach (var i in report.Input.Validation.Issues) issues.Add($"[validation · {i.Component}] {i.Message}");
        foreach (var i in report.Input.Analysis?.Issues ?? []) issues.Add($"[analysis · {i.Component}] {i.Message}");
        foreach (var i in report.Input.Analysis?.Results?.VerificationEvidence?.Issues ?? []) issues.Add($"[engineering verification · {i.Component}] {i.Message}");
        sb.Append("<details class=\"inventory\"><summary>Extraction issues (").Append(issues.Count).Append(")</summary><ul class=\"ul\">");
        if (issues.Count == 0) sb.Append("<li>No extraction issue was reported by the live ETABS read.</li>");
        else foreach (var issue in issues) sb.Append($"<li>{H(issue)}</li>");
        sb.Append("</ul></details>");

        sb.Append("<details class=\"inventory\"><summary>Rule coverage (").Append(report.Coverage.Count).Append(")</summary>");
        sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Check</th><th>Implementation state</th><th>Findings</th><th>Reason if deferred</th></tr></thead><tbody>");
        foreach (var coverage in report.Coverage)
            sb.Append($"<tr><td>{H(coverage.CheckId)}</td><td>{H(coverage.Implementation.ToString())}</td><td>{Num(coverage.Findings)}</td><td>{H(coverage.DeferredReason ?? "—")}</td></tr>");
        sb.Append("</tbody></table></div></details>");

        var limitations = Limitations(report);
        sb.Append("<details class=\"inventory\"><summary>Declared limitations (").Append(limitations.Count).Append(")</summary><ul class=\"ul\">");
        foreach (var limitation in limitations) sb.Append($"<li>{H(limitation)}</li>");
        sb.Append("</ul></details>");
        sb.Append("<p class=\"panel-note\">Coverage is reported per data set. A check without a finding is not a verified pass, and rules marked not implemented are never inferred.</p></div>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ sheets

    private static string SubmissionSheet(string project, string fileName, string etabs, string ran, IReadOnlyList<(string Label, CheckStatus Status, string Basis)> matrix)
    {
        var sb = new StringBuilder("<section class=\"sheet submission-sheet\" id=\"submission-sheet\">");
        sb.Append("<div class=\"sheet-bar\">DRAFT · ENGINEER REVIEW AND SIGN-OFF REQUIRED</div>");
        sb.Append("<h1>Structural Design &amp; Peer-Review Sign-Off Sheet</h1>");
        sb.Append($"<p class=\"sheet-sub\">Project {H(project)} · model {H(fileName)} · ETABS {H(etabs)} · inspection run {H(ran)}</p>");
        sb.Append("<p class=\"sheet-quote\">This computer model has been inspected for structural integrity, code compliance, and numerical stability in accordance with ASCE 7-16 and ACI 318-19.</p>");
        sb.Append("<h2>Verification matrix</h2><table><thead><tr><th>Criterion</th><th>Result</th><th>Traceable basis</th></tr></thead><tbody>");
        foreach (var (label, status, basis) in matrix) sb.Append($"<tr><td>{H(label)}</td><td>{Badge(status)}</td><td>{H(basis)}</td></tr>");
        sb.Append("</tbody></table>");
        sb.Append(SignatureBlock());
        sb.Append("<p class=\"sheet-foot\">The report is a read-only record of the live ETABS model and is not a design. Any value without a traceable ETABS source stays “not verified” until an engineer signs it off.</p>");
        sb.Append("</section>");
        return sb.ToString();
    }

    private static string SignoffPanel(string project, string fileName, string etabs, string ran, IReadOnlyList<(string Label, CheckStatus Status, string Basis)> matrix)
    {
        var sb = new StringBuilder("<div class=\"sheet\" id=\"sec-signoff\">");
        sb.Append("<div class=\"sheet-bar\">DRAFT · ENGINEER REVIEW AND SIGN-OFF REQUIRED</div>");
        sb.Append("<h1>Structural Design &amp; Peer-Review Sign-Off Sheet</h1>");
        sb.Append($"<p class=\"sheet-sub\">Project {H(project)} · model {H(fileName)} · ETABS {H(etabs)} · inspection run {H(ran)}</p>");
        sb.Append("<p class=\"sheet-quote\">This computer model has been inspected for structural integrity, code compliance, and numerical stability in accordance with ASCE 7-16 and ACI 318-19.</p>");
        sb.Append("<h2>Verification matrix</h2><table><thead><tr><th>Criterion</th><th>Result</th><th>Traceable basis</th></tr></thead><tbody>");
        foreach (var (label, status, basis) in matrix) sb.Append($"<tr><td>{H(label)}</td><td>{Badge(status)}</td><td>{H(basis)}</td></tr>");
        sb.Append("</tbody></table>");
        sb.Append(SignatureBlock());
        sb.Append("<p class=\"sheet-foot\">Signing confirms the engineer's independent verification of the values listed above against the ETABS model and the applicable criteria.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string SignatureBlock()
    {
        var sb = new StringBuilder("<div class=\"sign-grid\">");
        foreach (var role in new[] { "Engineer of Record", "Peer reviewer" })
        {
            sb.Append($"<div class=\"sign\"><strong>{H(role)}</strong>");
            sb.Append("<span>Name / License #</span><span>Signature / professional stamp</span><span class=\"line\"></span></div>");
        }
        return sb.Append("</div>").ToString();
    }

    // ------------------------------------------------------------------ overview blocks

    private static IReadOnlyList<(string Label, CheckStatus Status, string Basis)> Matrix(IReadOnlyList<Finding> findings)
    {
        var criteria = new (string Label, string CheckId)[]
        {
            ("Modal mass participation ≥ 90%", "SEIS-004"),
            ("Story drift ≤ limit (amplified Δ/h)", "SEIS-006"),
            ("Base shear scaling ≥ 100%", "AUD-002"),
            ("P-Delta stability θ ≤ θ max", "SEIS-017"),
            ("Column design ratio ≤ 1", "COL-004"),
            ("Beam design ratio ≤ 1", "BEAM-001"),
            ("Geometry, loads and analysis sanity", "SAN-001")
        };
        var rows = new List<(string, CheckStatus, string)>();
        foreach (var (label, checkId) in criteria)
        {
            var matches = findings.Where(f => f.CheckId.Equals(checkId, StringComparison.OrdinalIgnoreCase)).ToArray();
            rows.Add((label, matches.Length == 0 ? CheckStatus.NOT_VERIFIED : WorstStatus(matches), $"{checkId}: {matches.Length} finding(s)"));
        }
        return rows;
    }

    private sealed record Rec(string Text, string CheckId, string? Element, string? Story, string Title, Severity Severity, string SeverityClass);

    private static IReadOnlyList<Rec> Recommendations(IReadOnlyList<Finding> findings)
    {
        var list = new List<Rec>();
        foreach (var finding in findings.OrderByDescending(f => f.Severity).ThenBy(f => f.CheckId, StringComparer.Ordinal))
        {
            if (finding.Status is CheckStatus.PASS or CheckStatus.NOT_APPLICABLE) continue;
            var severity = finding.Severity.ToString().ToLowerInvariant();
            if (finding.Recommendations.Count == 0)
                list.Add(new(finding.Title, finding.CheckId, finding.ElementId, finding.Story, finding.Title, finding.Severity, severity));
            else
                foreach (var text in finding.Recommendations)
                    list.Add(new(text, finding.CheckId, finding.ElementId, finding.Story, finding.Title, finding.Severity, severity));
        }
        return list;
    }

    private static string ModalMini(InspectionReport report)
    {
        var modal = report.Input.Analysis?.Results?.ModalParticipation?.Items;
        if (modal is null || modal.Count == 0) return "<tbody><tr><td colspan=\"3\" class=\"muted\">Unavailable</td></tr></tbody>";
        var sb = new StringBuilder("<thead><tr><th>Mode</th><th>ΣUX</th><th>ΣUY</th></tr></thead><tbody>");
        foreach (var m in modal)
            sb.Append($"<tr><td>{Num(StepDisplay(m, report.Input.Analysis?.Results))}</td><td>{Pct(m.SumUX)}</td><td>{Pct(m.SumUY)}</td></tr>");
        return sb.Append("</tbody>").ToString();
    }

    private static string ModalSummary(InspectionReport report)
    {
        var modal = report.Input.Analysis?.Results?.ModalParticipation?.Items;
        if (modal is null || modal.Count == 0) return "<span>Modal results unavailable</span>";
        var last = modal[^1];
        var target = Target(report);
        var ok = last.SumUX >= target && last.SumUY >= target;
        return $"<span>ΣUX {Pct(last.SumUX)}</span><span>ΣUY {Pct(last.SumUY)}</span><span>Target {Pct(target)}</span>{Badge(ok ? CheckStatus.PASS : CheckStatus.WARNING)}";
    }

    private static string DriftBars(InspectionReport report)
    {
        var results = report.Input.Analysis?.Results;
        var drifts = results?.StoryDrifts?.Items;
        if (drifts is null || drifts.Count == 0) return "<p class=\"empty\">Story drift results unavailable.</p>";
        var seismic = report.Configuration.Settings.SeismicVerification;
        var limit = seismic?.DriftLimit;
        var sb = new StringBuilder();
        foreach (var d in drifts)
        {
            var amplified = d.Drift * (seismic is null || seismic.Ie == 0 ? 1 : seismic.Cd / seismic.Ie);
            var ratio = limit is { } lim && lim > 0 && double.IsFinite(amplified) ? amplified / lim : (double?)null;
            var width = ratio is { } r && double.IsFinite(r) ? Math.Clamp(r * 100, 1, 100) : 0;
            var color = d.Direction.Equals("Y", StringComparison.OrdinalIgnoreCase) ? "#f59e0b" : "#22d3ee";
            sb.Append("<div class=\"dr-row\">");
            sb.Append($"<span>{H(d.Story)} · {H(d.Case)} · {H(d.Direction)}</span>");
            sb.Append($"<div class=\"dr-track\"><i style=\"width:{width.ToString("0.#", Ci)}%;background:{color}\"></i></div>");
            sb.Append($"<b>{Num(amplified)}</b></div>");
        }
        return sb.ToString();
    }

    private static string LimitText(InspectionReport report)
    {
        var seismic = report.Configuration.Settings.SeismicVerification;
        return seismic is null ? "not configured" : Num(seismic.DriftLimit);
    }

    // ------------------------------------------------------------------ analysis mapping

    private static double Target(InspectionReport report)
    {
        var value = report.Configuration.Settings.SeismicVerification?.SpectrumMinimumRatio;
        return value is { } v && double.IsFinite(v) && v > 0 ? v : 0.9;
    }

    private static string TargetText(InspectionReport report) => Pct(Target(report));

    private static double StepDisplay(ModalParticipationResult mode, AnalysisResultsData? results)
    {
        if (double.IsFinite(mode.StepNumber) && mode.StepNumber > 0) return mode.StepNumber;
        return ResolveModalStep(mode, results) ?? double.NaN;
    }

    private static double? ResolveModalStep(ModalParticipationResult mode, AnalysisResultsData? results)
    {
        var table = results?.Extended?.Tables?.FirstOrDefault(t => t.Key.Contains("Modal Periods", StringComparison.OrdinalIgnoreCase));
        if (table is null) return null;
        double? best = null;
        var bestScore = double.PositiveInfinity;
        var ambiguous = false;
        foreach (var row in table.Rows)
        {
            if (!TextMatches(row, "Case", mode.Case)) continue;
            if (Number(row, "Mode") is not { } step) continue;
            var frequency = Number(row, "Frequency");
            var period = Number(row, "Period");
            double score;
            if (frequency is { } f && mode.Period > 0 && double.IsFinite(f)) score = Math.Abs(f - 1 / mode.Period);
            else if (period is { } p && double.IsFinite(mode.Period)) score = Math.Abs(p - mode.Period);
            else continue;
            if (score < bestScore - 1e-12) { bestScore = score; best = step; ambiguous = false; }
            else if (score <= bestScore + 1e-12) ambiguous = true;
        }
        return ambiguous ? null : best;
    }

    private static double? SafeInverse(double period) =>
        double.IsFinite(period) && Math.Abs(period) > 1e-12 ? 1 / period : null;

    private static IReadOnlyList<(string Name, double BaseShear, double WeightUsed, double CoeffUsed)> AutoSeismic(InspectionReport report)
    {
        var patterns = new List<(string, double, double, double)>();
        foreach (var table in report.Input.Analysis?.Results?.Extended?.Tables ?? [])
        {
            if (!table.Key.Contains("Auto Seismic", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var row in table.Rows)
            {
                var name = row.GetValueOrDefault("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Number(row, "BaseShear") is not { } shear) continue;
                if (Number(row, "WeightUsed") is not { } weight) continue;
                if (Number(row, "CoeffUsed") is not { } coefficient) continue;
                patterns.Add((name, shear, weight, coefficient));
            }
        }
        return patterns;
    }

    private static (double BaseShear, double WeightUsed, double CoeffUsed)? MappedPattern(InspectionReport report, string loadCase)
    {
        var names = (report.Input.Validation.LoadCases?.Items ?? [])
            .Where(c => c.Name.Equals(loadCase, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Loads ?? [])
            .Where(l => l.Type.Equals("Load", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return null;
        var matches = AutoSeismic(report).Where(p => names.Contains(p.Name)).ToArray();
        return matches.Length == 1 ? (matches[0].BaseShear, matches[0].WeightUsed, matches[0].CoeffUsed) : null;
    }

    private static string? MappedDirection(InspectionReport report, string loadCase)
    {
        foreach (var axis in report.Configuration.Settings.SeismicVerification?.Axes ?? [])
        {
            if (axis.ElasticCase.Equals(loadCase, StringComparison.OrdinalIgnoreCase)) return axis.Direction;
            if (axis.SpectrumCase is { Length: > 0 } spectrum && spectrum.Equals(loadCase, StringComparison.OrdinalIgnoreCase)) return axis.Direction;
        }
        return null;
    }

    private static Finding? DriftFinding(InspectionReport report, string story, string loadCase, string direction) =>
        Match(report, "SEIS-006", story, loadCase, direction);

    private static Finding? StabilityFinding(InspectionReport report, string story, string loadCase, string direction) =>
        Match(report, "SEIS-017", story, loadCase, direction);

    private static Finding? Match(InspectionReport report, string checkId, string story, string loadCase, string direction)
    {
        var key = $"{story}/{loadCase}/{direction}";
        return report.Findings.FirstOrDefault(f => f.CheckId.Equals(checkId, StringComparison.OrdinalIgnoreCase) && f.Story == story &&
            (f.ElementId == key || ((f.LoadCaseOrCombination ?? loadCase).Equals(loadCase, StringComparison.OrdinalIgnoreCase) &&
                (f.ElementId ?? string.Empty).EndsWith("/" + direction, StringComparison.OrdinalIgnoreCase))));
    }

    private static string PDeltaText(InspectionReport report, string loadCase)
    {
        var settings = report.Input.Analysis?.Definitions?.GeometricNonlinearity?.Items;
        if (settings is null || settings.Count == 0) return "Not read from the live model";
        var match = settings.FirstOrDefault(s => s.Case.Equals(loadCase, StringComparison.OrdinalIgnoreCase));
        if (match is null) return "No geometric nonlinearity setting for this case";
        return match.Meaning ?? $"Raw option {match.RawOption}";
    }

    // ------------------------------------------------------------------ helpers

    private static bool TextMatches(IReadOnlyDictionary<string, string> row, string field, string value) =>
        row.TryGetValue(field, out var found) && found.Trim().Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);

    private static double? Number(IReadOnlyDictionary<string, string> row, string field) =>
        row.TryGetValue(field, out var text) && double.TryParse(text, NumberStyles.Float, Ci, out var value) && double.IsFinite(value)
            ? value : null;

    private static string Label(CheckStatus status) => status switch
    {
        CheckStatus.PASS => "Pass",
        CheckStatus.FAIL => "Fail",
        CheckStatus.WARNING => "Warning",
        CheckStatus.NOT_VERIFIED => "Not verified",
        CheckStatus.SKIPPED => "Skipped",
        CheckStatus.NOT_APPLICABLE => "Not applicable",
        _ => "Error"
    };

    private static string Badge(CheckStatus status) => $"<span class=\"bdg\" data-status=\"{status}\">{H(Label(status))}</span>";

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.CRITICAL => "Critical",
        Severity.HIGH => "High",
        Severity.MEDIUM => "Medium",
        Severity.LOW => "Low",
        _ => "Informational"
    };

    private static string ModuleKey(string module)
    {
        var head = module.Split('-')[0].Trim();
        return string.IsNullOrEmpty(head) ? "OTHER" : head.ToUpperInvariant();
    }

    private static string Ring(double share, string color, int count)
    {
        var ratio = double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
        var text = count > 0 ? count.ToString(Ci) : (ratio * 100).ToString("0", Ci) + "%";
        return $"<div class=\"ring\" style=\"--p:{(ratio * 360).ToString("0.#", Ci)}deg;--c:{color}\"><b>{text}</b></div>";
    }

    private static double ShareValue(int count, int total) => total <= 0 ? 0 : (double)count / total;

    private static string Share(int count, int total) => total <= 0 ? "0%" : Math.Round(100.0 * count / total).ToString("0", Ci) + "%";

    private static string Num(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("G15", Ci) : "—";

    private static string Qty(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.###", Ci) : "—";

    private static string Pct(double? value) => value is { } v && double.IsFinite(v) ? Num(v * 100) + " %" : "—";

    private static string H(string? value) => value is null ? string.Empty : value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Ico(string path, string? style = null) =>
        $"<svg class=\"i\" viewBox=\"0 0 24 24\"{(style is null ? string.Empty : $" style=\"{style}\"")}><path d=\"{path}\"/></svg>";

    private static int Worst(CheckStatus status) => status switch
    {
        CheckStatus.FAIL => 6,
        CheckStatus.ERROR => 5,
        CheckStatus.WARNING => 4,
        CheckStatus.NOT_VERIFIED => 3,
        CheckStatus.SKIPPED => 2,
        CheckStatus.NOT_APPLICABLE => 1,
        _ => 0
    };

    private static CheckStatus WorstStatus(IEnumerable<Finding> findings) =>
        findings.OrderByDescending(f => Worst(f.Status)).First().Status;

    private static bool Finite(Coordinate point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static string ProjectName(ConfigurationInput configuration)
    {
        var project = configuration.Settings.Project;
        if (project is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
            return text;
        return "Untitled structural model";
    }

    private static IReadOnlyList<SvgPlanGenerator.FloorPlan> SafePlans(InspectionReport report)
    {
        try { return SvgPlanGenerator.RenderPlans(report); }
        catch (InvalidOperationException) { return []; }
    }

    private static string UniqueIds(string markup, int index) => markup
        .Replace("plan-title", $"story-{index}-plan-title", StringComparison.Ordinal)
        .Replace("plan-description", $"story-{index}-plan-description", StringComparison.Ordinal);

    private static IReadOnlyList<string> Limitations(InspectionReport report)
    {
        var model = report.Input.Model;
        var list = new List<string>();
        Add("Stories", model.Stories is null ? ["Story definitions are unavailable."] : null);
        Add("Joints", model.Joints?.Limitations);
        Add("Frames", model.Frames?.Limitations);
        Add("Areas", model.Areas?.Limitations);
        Add("Materials", model.Materials?.Limitations);
        foreach (var issue in model.Issues) list.Add($"Extraction: [{issue.Component}] {issue.Message}");
        foreach (var limitation in report.Input.Validation.Issues.Select(i => $"Validation: [{i.Component}] {i.Message}")) list.Add(limitation);
        if (list.Count == 0) list.Add("No limitation was reported by the live ETABS read.");
        return list;

        void Add(string name, IReadOnlyList<string>? limits)
        {
            if (limits is null) { list.Add($"{name}: data set is unavailable."); return; }
            foreach (var limit in limits) list.Add($"{name}: {limit}");
        }
    }

    // ------------------------------------------------------------------ geometry & json

    private static string GeometryJson(InspectionReport report)
    {
        var model = report.Input.Model;
        var joints = (model.Joints?.Items ?? []).ToDictionary(j => j.Id, StringComparer.Ordinal);
        var frames = model.Frames?.Items ?? [];
        var areas = model.Areas?.Items ?? [];
        var frameIds = frames.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var areaIds = areas.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var jointIds = joints.Keys.ToHashSet(StringComparer.Ordinal);
        var statuses = Statuses(report, frameIds, areaIds, jointIds);

        var sb = new StringBuilder("{\"stories\":[");
        var stories = model.Stories?.Stories ?? [];
        for (var i = 0; i < stories.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"name\":{J(stories[i].Name)},\"z\":{JN(stories[i].Elevation)}}}");
        }
        sb.Append("],\"elements\":[");
        var first = true;
        foreach (var frame in frames)
        {
            var start = joints.GetValueOrDefault(frame.StartJoint)?.Position ?? frame.Start;
            var end = joints.GetValueOrDefault(frame.EndJoint)?.Position ?? frame.End;
            if (!Finite(start) || !Finite(end)) continue;
            if (!first) sb.Append(',');
            first = false;
            var key = "Frame:" + frame.Id;
            sb.Append($"{{\"type\":\"Frame\",\"id\":{J(frame.Id)},\"story\":{J(frame.Story)},");
            sb.Append($"\"status\":{J(StatusText(statuses, key))},\"p\":[{Point(start)},{Point(end)}]}}");
        }
        foreach (var area in areas)
        {
            var vertices = area.Joints.Select(id => joints.GetValueOrDefault(id)?.Position)
                .Where(p => p is not null && Finite(p)).Select(p => Point(p!)).ToArray();
            if (vertices.Length < 3) continue;
            if (!first) sb.Append(',');
            first = false;
            var key = "Area:" + area.Id;
            sb.Append($"{{\"type\":\"Area\",\"id\":{J(area.Id)},\"story\":{J(area.Story)},");
            sb.Append($"\"status\":{J(StatusText(statuses, key))},\"p\":[{string.Join(",", vertices)}]}}");
        }
        sb.Append("],\"slabs\":[],\"joints\":[");
        var firstJoint = true;
        foreach (var joint in joints.Values)
        {
            if (!Finite(joint.Position)) continue;
            if (!firstJoint) sb.Append(',');
            firstJoint = false;
            sb.Append(Point(joint.Position));
        }
        return sb.Append("]}").ToString();
    }

    private static string FindingsJson(InspectionReport report)
    {
        var model = report.Input.Model;
        var frameIds = (model.Frames?.Items ?? []).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var areaIds = (model.Areas?.Items ?? []).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var jointIds = (model.Joints?.Items ?? []).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder("[");
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            if (i > 0) sb.Append(',');
            var key = ElementKeys(f, frameIds, areaIds, jointIds).FirstOrDefault();
            sb.Append("{");
            sb.Append($"\"key\":{J(key)},\"id\":{J(f.Id)},\"check\":{J(f.CheckId)},\"module\":{J(ModuleKey(f.Module))},");
            sb.Append($"\"status\":{J(f.Status.ToString())},\"severity\":{J(f.Severity.ToString())},\"story\":{J(f.Story)},");
            sb.Append($"\"element\":{J(f.ElementId)},\"type\":{J(f.ElementType)},\"title\":{J(f.Title)},");
            sb.Append($"\"actual\":{JN(f.Actual ?? double.NaN)},\"limit\":{JN(f.Limit ?? double.NaN)},\"unit\":{J(f.Unit)}");
            sb.Append("}");
        }
        return sb.Append(']').ToString();
    }

    private static string MetaJson(string project, ModelIdentity identity, string units, string ran, int total, int totalChecks, int implemented, int notImplemented) =>
        "{\"project\":" + J(project) + ",\"path\":" + J(identity.FilePath) + ",\"etabs\":" + J(identity.EtabsVersion) +
        ",\"units\":" + J(units) + ",\"run\":" + J(ran) + ",\"total\":" + total.ToString(Ci) + ",\"totalChecks\":" + totalChecks.ToString(Ci) +
        ",\"implemented\":" + implemented.ToString(Ci) + ",\"notImplemented\":" + notImplemented.ToString(Ci) + "}";

    private static Dictionary<string, CheckStatus> Statuses(InspectionReport report, HashSet<string> frameIds, HashSet<string> areaIds, HashSet<string> jointIds)
    {
        var statuses = new Dictionary<string, CheckStatus>(StringComparer.Ordinal);
        foreach (var finding in report.Findings)
            foreach (var key in ElementKeys(finding, frameIds, areaIds, jointIds))
                if (!statuses.TryGetValue(key, out var previous) || Worst(finding.Status) > Worst(previous)) statuses[key] = finding.Status;
        return statuses;
    }

    private static IEnumerable<string> ElementKeys(Finding finding, HashSet<string> frameIds, HashSet<string> areaIds, HashSet<string> jointIds)
    {
        if (finding.ElementType == "FrameGroup")
        {
            foreach (var member in finding.AffectedElements)
                if (frameIds.Contains(member)) yield return "Frame:" + member;
            yield break;
        }
        var id = finding.ElementId;
        if (string.IsNullOrWhiteSpace(id)) yield break;
        var type = finding.ElementType == "Point" ? "Joint" : finding.ElementType;
        if (type == "JointPair")
        {
            foreach (var joint in id.Split('|')) if (jointIds.Contains(joint)) yield return "Joint:" + joint;
            yield break;
        }
        if (type is "Frame" or "Area" or "Joint")
        {
            var prefix = type + ":";
            var name = id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
            if (Set(type).Contains(name)) yield return prefix + name;
            yield break;
        }
        if (type is not null) yield break;
        var colon = id.IndexOf(':');
        if (colon > 0)
        {
            var prefix = id[..colon].ToLowerInvariant() switch
            { "frame" => "Frame", "area" => "Area", "joint" => "Joint", "point" => "Joint", _ => null };
            if (prefix is not null && Set(prefix).Contains(id[(colon + 1)..])) yield return prefix + ":" + id[(colon + 1)..];
            yield break;
        }
        var matches = new List<string>();
        if (frameIds.Contains(id)) matches.Add("Frame:" + id);
        if (areaIds.Contains(id)) matches.Add("Area:" + id);
        if (jointIds.Contains(id)) matches.Add("Joint:" + id);
        if (matches.Count == 1) yield return matches[0];

        HashSet<string> Set(string kind) => kind switch { "Frame" => frameIds, "Area" => areaIds, _ => jointIds };
    }

    private static string StatusText(Dictionary<string, CheckStatus> statuses, string key) =>
        statuses.TryGetValue(key, out var status) ? status.ToString() : "NO_FINDING";

    private static string Point(Coordinate point) => $"[{JN(point.X)},{JN(point.Y)},{JN(point.Z)}]";

    private static string J(string? value)
    {
        if (value is null) return "null";
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '<' => "\\u003c",
                '>' => "\\u003e",
                '&' => "\\u0026",
                '\'' => "\\u0027",
                _ => c < ' ' ? "\\u" + ((int)c).ToString("x4", Ci) : c.ToString()
            });
        return sb.Append('"').ToString();
    }

    private static string JN(double value) => double.IsFinite(value) ? value.ToString("R", Ci) : "null";

    // ------------------------------------------------------------------ navigation & assets

    private static readonly (string Page, string? Anchor, string Label, string Path)[] NavItems =
    [
        ("viewer", null, "Model overview", "M3 12l9-9 9 9M5 10v10h14V10"),
        ("findings", null, "Findings", "M4 5h16M4 12h16M4 19h10"),
        ("results", null, "Analysis results", "M4 20V10M10 20V4M16 20v-7M2 20h20"),
        ("plans", null, "Floor plans", "M3 3h18v18H3zM3 9h18M9 21V9"),
        ("quantities", null, "Quantities", "M12 2 2 7l10 5 10-5zM2 17l10 5 10-5M2 12l10 5 10-5"),
        ("evidence", null, "Evidence", "M4 4h16v16H4zM8 9h8M8 13h5"),
        ("signoff", null, "Sign-off", "M4 20h16M6 16l9-9 3 3-9 9H6z")
    ];

    private const string Css = """
        *{box-sizing:border-box}
        :root{--bg:#06101d;--side:#081424;--card:#0b1a2e;--card2:#0a1626;--line:#16304d;--line2:#122741;--text:#e3ecf7;--muted:#86a0bf;--dim:#56708f;--accent:#22d3ee;--accent2:#0ea5c6;--pass:#22c55e;--fail:#f43f5e;--warn:#f59e0b}
        html,body{margin:0;background:var(--bg);color:var(--text);font:13px/1.5 ui-sans-serif,"Segoe UI",Roboto,Helvetica,Arial,sans-serif}
        a{color:var(--accent);text-decoration:none;cursor:pointer}
        h1{font-size:17px;margin:0}
        h2{font-size:15px;margin:0}
        h3{font-size:13px;margin:0 0 8px;color:#cfe0f2}
        h4{font-size:12px;margin:14px 0 6px;color:var(--accent)}
        small{color:var(--muted);font-size:11px;display:block}
        main{padding:14px 20px 40px}
        .app{display:grid;grid-template-columns:200px minmax(0,1fr);min-height:100vh}
        aside{background:var(--side);border-right:1px solid var(--line);padding:14px 10px;position:relative;min-height:100vh}
        .burger{color:var(--accent)}
        nav.side{display:flex;flex-direction:column;gap:2px;margin-top:10px}
        nav.side a{display:flex;gap:9px;align-items:center;padding:8px 10px;border-radius:8px;color:#b7cbe2;font-size:12.5px}
        nav.side a.on{background:rgba(34,211,238,.12);color:#eaf6ff}
        nav.side.bot{border-top:1px solid var(--line);margin-top:12px;padding-top:8px}
        .i{fill:none;stroke:currentColor;stroke-width:1.7;stroke-linecap:round;stroke-linejoin:round;width:16px;height:16px;flex:0 0 auto}
        .top{display:flex;align-items:flex-start;gap:12px;padding:16px 20px 0;flex-wrap:wrap}
        .top p{margin:3px 0 0;color:var(--muted);font-size:12px}
        .sp{flex:1}
        .meta{display:flex;gap:22px;font-size:12px;color:var(--muted);flex-wrap:wrap}
        .meta b{color:var(--text);font-weight:500;margin-left:4px}
        .page[hidden]{display:none}
        .card{background:linear-gradient(180deg,rgba(13,31,54,.9),rgba(10,24,42,.9));border:1px solid var(--line);border-radius:10px;min-width:0}
        .kpis{display:grid;grid-template-columns:1.1fr .95fr 1fr 1fr 1fr;gap:12px;margin-bottom:12px}
        .kpi{display:flex;gap:16px;align-items:center;padding:14px 16px;min-height:104px}
        .kpi .ic{width:44px;height:44px;border-radius:9px;display:grid;place-items:center;flex:none}
        .kpi span{display:block;font-size:12.5px;color:#d5e2f1}
        .kpi strong{display:block;font-size:26px;line-height:1.25;font-variant-numeric:tabular-nums}
        .kpi small{display:block;color:var(--muted);font-size:11.5px}
        .kpi .bd{display:grid;grid-template-columns:repeat(3,auto);gap:2px 12px;font-size:11px;color:var(--muted);margin-top:4px}
        .kpi .bd span{font-size:11px;color:var(--muted)}
        .kpi .bd b{display:inline;font-size:11px;color:var(--text)}
        .lvl{font-size:15px;font-weight:600}
        .ring{position:relative;width:64px;height:64px;flex:none;border-radius:50%;background:conic-gradient(var(--c) var(--p),rgba(255,255,255,.08) 0)}
        .ring::after{content:'';position:absolute;inset:7px;border-radius:50%;background:var(--card)}
        .ring b{position:absolute;inset:0;display:grid;place-items:center;font-size:15px;font-weight:600;z-index:1}
        .grid{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(360px,1fr);gap:12px;margin-bottom:12px}
        .vh{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid var(--line2);flex-wrap:wrap}
        .seg{display:flex;gap:4px}
        .seg button{border:1px solid var(--line);background:var(--card2);padding:6px 14px;border-radius:6px;cursor:pointer;color:#b9cbe0;font-size:12px}
        .seg button.on{background:rgba(34,211,238,.16);border-color:var(--accent2);color:#fff}
        .sel{background:var(--card2);border:1px solid var(--line);border-radius:6px;padding:6px 10px;color:var(--text);font-size:12px;min-width:140px}
        .vh .sp{flex:1}
        .ib{width:30px;height:30px;display:grid;place-items:center;border:1px solid var(--line);background:var(--card2);border-radius:6px;cursor:pointer;color:#b9cbe0}
        .ib:hover{border-color:#24476d;color:#fff}
        .ib.on{border-color:var(--accent2);color:var(--accent)}
        .vbody{display:grid;grid-template-columns:minmax(0,1fr) 128px}
        .stage{position:relative;height:452px;overflow:hidden;border-radius:0 0 0 10px;background:radial-gradient(ellipse at 50% 55%,rgba(34,211,238,.06),transparent 65%)}
        .stage canvas{position:absolute;inset:0;width:100%;height:100%;cursor:grab;touch-action:none}
        .lead{position:absolute;inset:0;width:100%;height:100%;pointer-events:none;z-index:2}
        #cos{position:absolute;inset:0;z-index:3;pointer-events:none}
        .co{position:absolute;background:rgba(7,16,29,.9);border:1px solid var(--accent);border-radius:6px;padding:5px 9px;font-size:11px;line-height:1.35;width:170px;color:#c7d6e8;pointer-events:auto;cursor:pointer}
        .co b{display:block;color:#fff;font-size:12px}
        .co.fail{border-color:var(--fail)}.co.fail b{color:#fb7185}
        .co.warning{border-color:var(--warn)}.co.warning b{color:#fbbf24}
        .gz{position:absolute;left:14px;bottom:10px;font-size:11px;color:var(--dim);z-index:2}
        .side3{border-left:1px solid var(--line2);padding:12px;font-size:12px;display:flex;flex-direction:column;gap:14px}
        .side3 h4{font-size:12px;font-weight:500;color:var(--muted);margin:0 0 8px}
        .tg{display:flex;align-items:center;gap:8px;margin:8px 0;cursor:pointer;color:#c7d6e8}
        .tg i{width:12px;height:12px;border-radius:3px;display:inline-block}
        .lgd div{display:flex;align-items:center;gap:8px;margin:7px 0;color:var(--muted)}
        .lgd i{width:12px;height:12px;border-radius:3px;display:inline-block}
        .dg{display:flex;flex-direction:column;overflow:auto;max-height:520px}
        .dh{display:flex;align-items:center;gap:10px;padding:11px 14px;border-bottom:1px solid var(--line2)}
        .dh h3{font-size:15px;flex:1;margin:0}
        .dh span{font-size:12px;color:var(--muted)}
        .dt{display:flex;align-items:center;gap:12px;padding:12px 14px 6px;flex-wrap:wrap}
        .dt h2{font-size:17px}
        .dt .loc{margin-left:auto;font-size:12px;color:var(--muted)}
        .dc{padding:4px 14px 12px}
        .dc h4{font-size:13px;margin:6px 0 8px;color:#d5e2f1}
        .kt{width:100%;border-collapse:collapse;font-size:12px}
        .kt td{padding:5px 8px;border-bottom:1px solid var(--line2);vertical-align:top}
        .kt td:first-child{color:var(--muted);white-space:nowrap;width:42%}
        .r3{display:grid;grid-template-columns:1fr .95fr 1fr 1.25fr;gap:12px}
        .bc{padding:13px 15px}
        .bc h3{font-size:14px;margin:0 0 11px;display:flex;justify-content:space-between;align-items:center}
        .bc h3 a{font-size:11.5px;color:var(--accent);font-weight:500;cursor:pointer}
        .cm div{display:grid;grid-template-columns:1fr auto;gap:2px 8px;align-items:center;padding:6px 0;border-bottom:1px solid var(--line2);font-size:12px}
        .cm div:last-child{border:0}
        .cm small{grid-column:1/-1;color:var(--dim);font-size:10.5px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .mt{width:100%;border-collapse:collapse;font-size:11.5px}
        .mt th{color:var(--muted);font-weight:500;text-align:right;padding:4px 5px;border-bottom:1px solid var(--line)}
        .mt th:first-child,.mt td:first-child{text-align:left}
        .mt td{padding:4px 5px;text-align:right;font-variant-numeric:tabular-nums;border-bottom:1px solid var(--line2)}
        .ms{display:flex;gap:16px;margin-top:9px;font-size:11.5px;color:var(--muted);flex-wrap:wrap;align-items:center}
        .dl{display:flex;gap:14px;font-size:11px;color:var(--muted);margin-bottom:8px;flex-wrap:wrap}
        .dl span::before{content:'';display:inline-block;width:12px;height:3px;background:var(--c);margin-right:5px;vertical-align:middle}
        .dr{display:grid;gap:8px}
        .dr-row{display:grid;grid-template-columns:1fr 90px 54px;gap:8px;align-items:center;font-size:11.5px}
        .dr-track{height:9px;background:rgba(255,255,255,.05);border-radius:2px;overflow:hidden}
        .dr-track i{display:block;height:100%;border-radius:2px}
        .dr-row b{text-align:right;font-variant-numeric:tabular-nums}
        .rl{list-style:none;margin:0;padding:0;counter-reset:r}
        .rl li{display:grid;grid-template-columns:24px 1fr auto;gap:10px;align-items:center;padding:7px 0;border-bottom:1px solid var(--line2)}
        .rl li:last-child{border:0}
        .rl li::before{counter-increment:r;content:counter(r);width:24px;height:24px;border-radius:5px;background:rgba(34,211,238,.1);border:1px solid var(--line);display:grid;place-items:center;font-size:12px}
        .rl b{display:block;font-size:12.5px;font-weight:500}
        .rl small{display:block;color:var(--muted);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .sv{font-size:10.5px;font-weight:600;border-radius:4px;padding:3px 8px;white-space:nowrap;background:rgba(34,211,238,.18);color:#67e8f9}
        .sv.critical{background:rgba(244,63,94,.85);color:#fff}
        .sv.high{background:rgba(245,158,11,.85);color:#111}
        .sv.medium{background:rgba(234,179,8,.25);color:#fde047}
        .sv.low{background:rgba(34,211,238,.18);color:#67e8f9}
        .sv.informational{background:rgba(128,149,174,.2);color:#c3d2e3}
        .bdg{display:inline-flex;border-radius:4px;font-size:10.5px;font-weight:600;padding:2px 8px;white-space:nowrap;background:rgba(128,149,174,.16);color:#a9bbd1}
        .bdg[data-status="PASS"]{background:rgba(34,197,94,.15);color:#4ade80}
        .bdg[data-status="FAIL"],.bdg[data-status="ERROR"]{background:rgba(244,63,94,.17);color:#fb7185}
        .bdg[data-status="WARNING"]{background:rgba(245,158,11,.16);color:#fbbf24}
        .panel{background:var(--card);border:1px solid var(--line);border-radius:10px;margin-bottom:12px;overflow:hidden}
        .panel-h{display:flex;justify-content:space-between;gap:16px;flex-wrap:wrap;align-items:flex-end;padding:15px 16px}
        .panel-h h2{font-size:15px}
        p.sub{margin:3px 0 0;color:var(--muted);font-size:12px}
        p.sub.pad{padding:10px 16px 0;margin:0}
        .controls{display:flex;gap:8px;flex-wrap:wrap;align-items:flex-end}
        .controls label{display:flex;flex-direction:column;gap:4px;font-size:11px;color:var(--muted)}
        .controls select,.controls input{background:var(--card2);border:1px solid var(--line);border-radius:6px;padding:6px 10px;min-width:150px;color:var(--text)}
        .table-wrap{overflow:auto}
        .panel table{border-collapse:collapse;width:100%;font-size:12px}
        .panel th{background:rgba(34,211,238,.05);color:var(--muted);font-weight:500;text-align:left;padding:8px 12px;border-bottom:1px solid var(--line);white-space:nowrap}
        .panel td{padding:8px 12px;border-bottom:1px solid var(--line2);vertical-align:top}
        .panel td small{display:block;color:var(--muted);font-size:11px}
        .panel-note{padding:10px 16px 14px;margin:0;color:var(--muted);font-size:12px}
        .empty{padding:14px 16px;color:var(--muted)}
        td.desc{min-width:320px}
        td.check{font-weight:600;white-space:nowrap}
        details.inventory{padding:10px 16px;border-top:1px solid var(--line2)}
        details.inventory summary{cursor:pointer;font-weight:600}
        details.inventory[open] summary{margin-bottom:8px}
        .ul{margin:6px 0 0;padding-left:18px;font-size:12px;color:#c7d6e8}
        .ul li{margin:3px 0}
        .txt{font-size:12px;color:#c7d6e8;margin:0 0 10px}
        .muted{color:var(--muted)}
        .cmp{width:100%;border-collapse:collapse;font-size:12px}
        .cmp th{background:rgba(34,211,238,.06);color:var(--muted);font-weight:500;text-align:left;padding:7px 9px;border-bottom:1px solid var(--line)}
        .cmp td{padding:7px 9px;border-bottom:1px solid var(--line2);vertical-align:top}
        .cmp code{font:11.5px Consolas,monospace;color:#c7e9f5;overflow-wrap:anywhere}
        .cmp small{color:var(--dim);font:10.5px Consolas,monospace}
        .finding-detail summary{cursor:pointer;font-weight:500}
        .drawer{background:var(--card2);border:1px solid var(--line);border-radius:8px;padding:12px 14px;margin-top:8px}
        .drawer h4{font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted);margin:12px 0 4px}
        .drawer dl.metrics{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin:0}
        .drawer dt{font-size:10.5px;color:var(--muted)}
        .drawer dd{margin:0}
        .legend{display:flex;gap:16px;padding:0 16px 10px;font-size:12px;color:var(--muted);flex-wrap:wrap}
        .legend span::before{content:'';display:inline-block;width:10px;height:10px;border-radius:2px;background:var(--c);margin-right:6px}
        .plan-stage{border-top:1px solid var(--line2);padding:16px}
        .floor-plan h3{font-size:13px;margin:0 0 8px}
        .floor-plan svg{width:100%;height:440px;background:#071222;border:1px solid var(--line);border-radius:8px;display:block}
        .sheet{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:22px 24px;margin:14px 20px}
        .submission-sheet{margin:14px 20px 0}
        .sheet-bar{font-size:11px;font-weight:700;letter-spacing:.1em;color:var(--warn)}
        .sheet h1{font-size:20px;margin:6px 0 2px}
        .sheet h2{font-size:14px;margin:18px 0 8px}
        .sheet-sub{margin:2px 0 0;color:var(--muted);font-size:12px}
        .sheet-quote{margin:14px 0 4px;padding:10px 16px;border-left:3px solid var(--accent);background:var(--card2);font-style:italic;color:#c7d6e8}
        .sheet-foot{margin:16px 0 0;color:var(--muted);font-size:11.5px}
        .sheet table{border-collapse:collapse;width:100%;font-size:12px}
        .sheet th{background:rgba(34,211,238,.05);color:var(--muted);font-weight:500;text-align:left;padding:8px 12px;border-bottom:1px solid var(--line)}
        .sheet td{padding:8px 12px;border-bottom:1px solid var(--line2)}
        .sign-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px;margin-top:16px}
        .sign{border:1px solid var(--line);border-radius:10px;padding:14px}
        .sign strong{display:block;font-size:13px}
        .sign span{display:block;color:var(--muted);font-size:11px;margin-top:10px}
        .sign .line{border-top:1px solid var(--line);margin-top:4px;height:0}
        @media (max-width:1350px){.kpis{grid-template-columns:repeat(3,1fr)}.grid{grid-template-columns:1fr}.r3{grid-template-columns:1fr 1fr}}
        @media (max-width:820px){.app{grid-template-columns:1fr}aside{display:none}.kpis{grid-template-columns:1fr 1fr}.r3{grid-template-columns:1fr}.vbody{grid-template-columns:1fr}.side3{display:none}.sign-grid{grid-template-columns:1fr}}
        @media print{aside{display:none}.app{display:block}.page[hidden]{display:block}.card,.panel,.sheet{break-inside:avoid}}
        """;

    private const string ReportPolishCss = """
        :root{--bg:#0c1320;--side:#101a2b;--card:#131f31;--card2:#101a29;--line:#29394f;--line2:#233147;--text:#edf2fa;--muted:#a4b3c8;--dim:#91a3bb;--accent:#78d9ce;--accent2:#49acaa}
        html{scroll-behavior:smooth}body{font-size:14px;line-height:1.6}
        main{max-width:1800px;margin:auto;padding:24px 32px 56px}.top{padding:26px 32px 8px}.top h1{font-size:22px;letter-spacing:-.025em}.meta{gap:6px 18px;max-width:700px}.meta span{overflow-wrap:anywhere}
        aside{position:sticky;top:0;height:100vh;min-height:0;padding:24px 14px}.burger{padding:0 12px 18px}nav.side{gap:6px}nav.side a{padding:11px 12px;border:1px solid transparent}nav.side a.on{border-color:#355b63;background:#18343e;color:#a6f0e6}
        .review-intro{display:flex;justify-content:space-between;align-items:center;gap:24px;margin:4px 0 24px}.eyebrow{font-size:10px;letter-spacing:.13em;font-weight:700;color:var(--accent)}.review-intro h2{font-size:30px;letter-spacing:-.035em;margin:4px 0}.review-intro p{margin:0;color:var(--muted)}.primary-action{display:inline-flex;gap:24px;background:var(--accent);color:#102627;border-radius:9px;padding:12px 18px;font-weight:700;white-space:nowrap}
        .card,.panel{background:var(--card);border-radius:14px;box-shadow:0 5px 20px #00000010}.kpis{gap:14px;margin-bottom:20px}.kpi{padding:18px;align-items:flex-start}.kpi strong{font-size:30px}.kpi .bd{grid-template-columns:repeat(2,auto)}.ring{width:52px;height:52px}.kpi span{color:var(--text)}
        .panel-note{background:var(--card2);border:1px solid var(--line2);border-left:3px solid var(--accent2);border-radius:8px;padding:13px 16px;margin:0 0 12px;font-size:13px}.panel-note b{color:var(--text)}
        .verification-overview{display:grid;grid-template-columns:repeat(auto-fit,minmax(165px,1fr));gap:10px;margin:18px 0 24px}.verification-tile{display:flex;flex-direction:column;gap:4px;padding:16px;border:1px solid var(--line);border-radius:11px;background:var(--card);color:var(--muted);font-size:12px;transition:border-color .15s,transform .15s}.verification-tile strong{font-size:15px;color:var(--text)}.verification-tile:hover{border-color:var(--accent);transform:translateY(-2px)}
        .grid,.r3{gap:18px;margin-bottom:20px}.bc{padding:20px}.bc h3,h3{color:var(--text)}.cm small,.rl small{white-space:normal}.stage{height:480px}.panel-h{padding:22px}.panel-h h2{font-size:19px}.controls input{min-width:250px}.controls input,.controls select,.sel{padding:9px 12px}.panel th{padding:12px 16px;background:#1b2b40}.panel td{padding:14px 16px}.panel tbody tr:hover{background:#ffffff03}.table-wrap{max-height:75vh}.panel thead th{position:sticky;top:0;z-index:2}.bdg{padding:4px 9px;border-radius:6px;font-size:11px}.drawer{padding:20px}.drawer dl.metrics{gap:14px}.finding-detail summary{line-height:1.65}.drawer dd,.txt,.cmp code{overflow-wrap:anywhere}.drawer dd{font-variant-numeric:tabular-nums}.drawer h4{margin-top:20px}
        a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible,summary:focus-visible{outline:3px solid var(--accent);outline-offset:4px}button,input,select{font:inherit}
        @media(max-width:1100px){.app{grid-template-columns:170px minmax(0,1fr)}main{padding:20px}.top{padding:22px 20px}.kpis{grid-template-columns:repeat(2,minmax(0,1fr))}}
        @media(max-width:820px){.app{display:block}aside{display:block;position:static;height:auto;padding:10px 16px;border-bottom:1px solid var(--line)}.burger{display:none}nav.side{flex-direction:row;overflow:auto;margin:0}nav.side a{flex:none}nav.side.bot{display:none}.review-intro{align-items:flex-start;flex-direction:column}.review-intro h2{font-size:26px}.meta{font-size:11px}.top{display:block}.kpi{gap:10px;padding:14px}.kpi .ring,.kpi .ic{display:none}.controls,.controls label,.controls input{width:100%}.verification-overview{grid-template-columns:repeat(2,minmax(0,1fr))}.stage{height:360px}}
        @media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.verification-tile{transition:none}}
        @media print{html,body{background:white;color:#172033;font-size:10pt}aside,.primary-action,.controls{display:none}.app{display:block}main{padding:0;max-width:none}.card,.panel,.panel-note,.verification-tile,.drawer{background:white;color:#172033;box-shadow:none;border-color:#ccd3dd}.table-wrap{max-height:none;overflow:visible}.panel thead th{position:static;background:#eef2f6;color:#172033}.page[hidden]{display:block}.panel,.card{break-inside:auto}.panel tr,.verification-tile{break-inside:avoid}.meta b,h3,.verification-tile strong,.kpi span{color:#172033}.review-intro{margin-top:20px}.stage{background:#101a29}a{color:#12625b}}
        """;

    private const string Js = """
        (function(){
        'use strict';
        var G=window.GEOMETRY||{stories:[],elements:[],joints:[]};
        var F=window.FINDINGS||[];
        var pages=[].slice.call(document.querySelectorAll('.page'));
        var navs=[].slice.call(document.querySelectorAll('nav.side a[data-go]'));
        function esc(v){return v===null||v===undefined?'':String(v).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
        function num(v){return typeof v==='number'&&isFinite(v)?String(v):'\u2014';}
        function show(page,anchor){
        pages.forEach(function(p){p.hidden=p.getAttribute('data-page')!==page;});
        navs.forEach(function(a){a.classList.toggle('on',a.getAttribute('data-go')===page);});
        if(anchor){var t=document.getElementById(anchor);if(t)t.scrollIntoView({behavior:'smooth',block:'start'});}
        else window.scrollTo(0,0);
        }
        document.addEventListener('click',function(e){
        var el=e.target;
        var a=el&&el.closest?el.closest('[data-go]'):null;
        if(a){e.preventDefault();show(a.getAttribute('data-go'),a.getAttribute('data-anchor'));var check=a.getAttribute('data-check');if(check){var search=document.getElementById('finding-search');if(search){search.value=check;document.getElementById('status-filter').value='ALL';document.getElementById('module-filter').value='ALL';filter();}}}
        });
        var printBtn=document.getElementById('print');
        if(printBtn)printBtn.addEventListener('click',function(){window.print();});
        show('viewer',null);
        var rows=[].slice.call(document.querySelectorAll('table.findings tbody tr'));
        var statusSel=document.getElementById('status-filter');
        var moduleSel=document.getElementById('module-filter');
        var searchBox=document.getElementById('finding-search');
        function filter(){
        var s=statusSel?statusSel.value:'ALL';
        var m=moduleSel?moduleSel.value:'ALL';
        var q=searchBox?searchBox.value.replace(/\s+/g,' ').trim().toLowerCase():'';
        var n=0;
        rows.forEach(function(r){
        var ok=(s==='ALL'||r.getAttribute('data-status')===s)&&(m==='ALL'||r.getAttribute('data-module')===m)&&(q===''||(r.getAttribute('data-search')||'').indexOf(q)>=0);
        r.hidden=!ok;if(ok)n+=1;
        });
        var c=document.querySelector('[data-f="count"]');if(c)c.textContent=String(n);
        var e=document.querySelector('[data-f="empty"]');if(e)e.hidden=n>0;
        }
        [statusSel,moduleSel,searchBox].forEach(function(el){if(el)el.addEventListener('input',filter);});
        if(rows.length)filter();
        var dg=document.getElementById('dg');
        if(dg){
        var open=[];
        for(var i=0;i<F.length;i+=1){if(F[i].status!=='PASS'&&F[i].status!=='NOT_APPLICABLE')open.push(F[i]);}
        var out=['<div class="dh"><h3>Finding diagnostics</h3><span>'+open.length+' item(s)</span></div>'];
        if(open.length===0)out.push('<div class="dc muted">No open finding. Gray in the model does not establish safety.</div>');
        for(var j=0;j<open.length&&j<40;j+=1){
        var f=open[j];
        out.push('<div class="dc"><div class="dt"><h2>'+esc(f.check)+'</h2><span class="bdg" data-status="'+esc(f.status)+'">'+esc(f.status)+'</span><span class="loc">'+esc(f.element||'model')+(f.story?' \u00b7 '+esc(f.story):'')+'</span></div>');
        out.push('<h4>'+esc(f.title)+'</h4>');
        out.push('<table class="kt"><tbody><tr><td>Module</td><td>'+esc(f.module)+'</td></tr><tr><td>Severity</td><td>'+esc(f.severity)+'</td></tr><tr><td>Actual</td><td>'+num(f.actual)+'</td></tr><tr><td>Limit</td><td>'+num(f.limit)+'</td></tr></tbody></table></div>');
        }
        dg.innerHTML=out.join('');
        }
        var sel=document.getElementById('story-selector');
        if(sel){
        var planEls=[].slice.call(document.querySelectorAll('[data-story-plan]'));
        var pick=function(i){planEls.forEach(function(p){p.hidden=p.getAttribute('data-story-plan')!==String(i);});};
        sel.addEventListener('change',function(){pick(sel.value);});
        pick(sel.value);
        }
        var cv=document.getElementById('c3');
        if(cv&&cv.getContext){
        var ctx=cv.getContext('2d');
        var els=G.elements||[];var joints=G.joints||[];var stories=G.stories||[];
        var rotX=-0.5,rotY=0.7,zoom=1,panX=0,panY=0,showFrames=true,showAreas=true,showJoints=true,callouts=true,storyName=null;
        var W=600,H=452;
        var minX=null,maxX=0,minY=null,maxY=0,minZ=null,maxZ=0,cx=0,cy=0,cz=0,span=1;
        function acc(p){
        if(minX===null){minX=maxX=p[0];minY=maxY=p[1];minZ=maxZ=p[2];return;}
        if(p[0]<minX)minX=p[0];if(p[0]>maxX)maxX=p[0];
        if(p[1]<minY)minY=p[1];if(p[1]>maxY)maxY=p[1];
        if(p[2]<minZ)minZ=p[2];if(p[2]>maxZ)maxZ=p[2];
        }
        for(var a=0;a<els.length;a+=1){var ps=els[a].p||[];for(var b=0;b<ps.length;b+=1)acc(ps[b]);}
        for(var c2=0;c2<joints.length;c2+=1)acc(joints[c2]);
        if(minX===null){minX=0;maxX=1;minY=0;maxY=1;minZ=0;maxZ=1;}
        cx=(minX+maxX)/2;cy=(minY+maxY)/2;cz=(minZ+maxZ)/2;
        span=Math.max(maxX-minX,maxY-minY,maxZ-minZ,1);
        function color(s){return s==='FAIL'?'#f43f5e':(s==='WARNING'?'#f59e0b':'#8ea3bd');}
        function rgbaOf(h,al){var n=parseInt(h.slice(1),16);return 'rgba('+((n>>16)&255)+','+((n>>8)&255)+','+(n&255)+','+al+')';}
        function project(p){
        var x=p[0]-cx,y=p[1]-cy,z=p[2]-cz;
        var cY=Math.cos(rotY),sY=Math.sin(rotY);
        var x1=x*cY-y*sY,y1=x*sY+y*cY;
        var cXa=Math.cos(rotX),sX=Math.sin(rotX);
        var y2=y1*cXa-z*sX,z2=y1*sX+z*cXa;
        var s=zoom*Math.min(W,H)/(span*1.8);
        return [W/2+x1*s+panX,H/2-y2*s+panY,z2];
        }
        function calloutsDraw(items){
        var lead=document.getElementById('lead'),cos=document.getElementById('cos');
        if(!lead||!cos)return;
        lead.innerHTML='';cos.innerHTML='';
        if(!callouts)return;
        var shown=0;
        for(var i=0;i<items.length&&shown<8;i+=1){
        var it=items[i];
        if(it.e.status!=='FAIL'&&it.e.status!=='WARNING')continue;
        shown+=1;
        var p=it.proj[0];
        var x=Math.max(8,Math.min(p[0]+18,W-186)),y=Math.max(8,Math.min(p[1]-12,H-58));
        var div=document.createElement('div');
        div.className='co '+(it.e.status==='FAIL'?'fail':'warning');
        div.style.left=x+'px';div.style.top=y+'px';
        div.innerHTML='<b>'+esc(it.e.id)+'</b><span>'+esc(it.e.status)+' '+esc(it.e.type)+'</span>';
        cos.appendChild(div);
        var ln=document.createElementNS('http://www.w3.org/2000/svg','line');
        ln.setAttribute('x1',p[0]);ln.setAttribute('y1',p[1]);ln.setAttribute('x2',x);ln.setAttribute('y2',y+12);
        ln.setAttribute('stroke',color(it.e.status));ln.setAttribute('stroke-width','1');ln.setAttribute('stroke-dasharray','3 2');
        lead.appendChild(ln);
        }
        }
        function draw(){
        if(!ctx)return;
        ctx.setTransform(1,0,0,1,0,0);
        ctx.clearRect(0,0,W,H);
        var items=[];
        for(var i=0;i<els.length;i+=1){
        var e=els[i];
        if(storyName&&e.story!==storyName)continue;
        if(e.type==='Area'&&!showAreas)continue;
        if(e.type==='Frame'&&!showFrames)continue;
        var pp=e.p||[];
        if(!pp.length)continue;
        var proj=[],depth=0;
        for(var k=0;k<pp.length;k+=1){var q=project(pp[k]);proj.push(q);depth+=q[2];}
        depth=depth/proj.length;
        items.push({e:e,proj:proj,depth:depth});
        }
        items.sort(function(a1,b1){return a1.depth-b1.depth;});
        for(var m2=0;m2<items.length;m2+=1){
        var it=items[m2],cl=color(it.e.status),pr=it.proj;
        if(it.e.type==='Area'){
        ctx.beginPath();
        for(var n2=0;n2<pr.length;n2+=1){if(n2===0)ctx.moveTo(pr[n2][0],pr[n2][1]);else ctx.lineTo(pr[n2][0],pr[n2][1]);}
        ctx.closePath();ctx.fillStyle=rgbaOf(cl,0.16);ctx.fill();
        ctx.strokeStyle=rgbaOf(cl,0.55);ctx.lineWidth=1;ctx.stroke();
        }
        else{
        ctx.beginPath();ctx.moveTo(pr[0][0],pr[0][1]);ctx.lineTo(pr[pr.length-1][0],pr[pr.length-1][1]);
        ctx.strokeStyle=cl;ctx.lineWidth=it.e.status==='FAIL'?2.2:1.6;ctx.stroke();
        }
        }
        if(showJoints){
        ctx.fillStyle='rgba(142,163,189,.65)';
        for(var o=0;o<joints.length;o+=1){var jp=project(joints[o]);ctx.fillRect(jp[0]-1,jp[1]-1,2,2);}
        }
        calloutsDraw(items);
        }
        var layers=document.getElementById('layers');
        if(layers){
        layers.innerHTML='<label class="tg" data-layer="frames"><i style="background:#22d3ee"></i>Frames</label>'+'<label class="tg" data-layer="areas"><i style="background:#22d3ee"></i>Areas</label>'+'<label class="tg" data-layer="joints"><i style="background:#22d3ee"></i>Joints</label>';
        layers.addEventListener('click',function(e){
        var l=e.target&&e.target.closest?e.target.closest('[data-layer]'):null;
        if(!l)return;
        var key=l.getAttribute('data-layer'),on;
        if(key==='frames'){showFrames=!showFrames;on=showFrames;}
        else if(key==='areas'){showAreas=!showAreas;on=showAreas;}
        else {showJoints=!showJoints;on=showJoints;}
        var sw=l.querySelector('i');if(sw)sw.style.background=on?'#22d3ee':'#334a66';
        draw();
        });
        }
        var storySel=document.getElementById('story');
        if(storySel){
        var opts=['<option value="">All stories</option>'];
        for(var s3=0;s3<stories.length;s3+=1)opts.push('<option value="'+esc(stories[s3].name)+'">'+esc(stories[s3].name)+'</option>');
        storySel.innerHTML=opts.join('');
        storySel.addEventListener('change',function(){storyName=storySel.value||null;draw();});
        }
        var cams=document.getElementById('cams');
        if(cams){
        cams.addEventListener('click',function(e){
        var btn=e.target&&e.target.closest?e.target.closest('[data-cam]'):null;
        if(!btn)return;
        [].slice.call(cams.querySelectorAll('button')).forEach(function(x){x.classList.toggle('on',x===btn);});
        var c4=btn.getAttribute('data-cam');
        if(c4==='top'){rotX=-1.45;rotY=0;}
        else if(c4==='front'){rotX=0;rotY=0;}
        else {rotX=-0.5;rotY=0.7;}
        panX=0;panY=0;draw();
        });
        }
        [].slice.call(document.querySelectorAll('[data-zoom]')).forEach(function(btn){
        btn.addEventListener('click',function(){
        var f2=parseFloat(btn.getAttribute('data-zoom'));
        zoom=Math.max(0.2,Math.min(8,zoom*f2));draw();
        });
        });
        var reset=document.getElementById('reset');
        if(reset)reset.addEventListener('click',function(){rotX=-0.5;rotY=0.7;zoom=1;panX=0;panY=0;draw();});
        var tco=document.getElementById('tco');
        if(tco)tco.addEventListener('click',function(){callouts=!callouts;tco.classList.toggle('on',callouts);draw();});
        var drag=null;
        cv.addEventListener('pointerdown',function(e){drag={x:e.clientX,y:e.clientY,pan:e.button===2};if(cv.setPointerCapture)cv.setPointerCapture(e.pointerId);});
        cv.addEventListener('pointermove',function(e){
        if(!drag)return;
        var dx=e.clientX-drag.x,dy=e.clientY-drag.y;
        drag.x=e.clientX;drag.y=e.clientY;
        if(drag.pan){panX+=dx;panY+=dy;}
        else{rotY+=dx*0.01;rotX=Math.max(-1.5,Math.min(1.5,rotX-dy*0.01));}
        draw();
        });
        cv.addEventListener('pointerup',function(){drag=null;});
        cv.addEventListener('pointerleave',function(){drag=null;});
        cv.addEventListener('contextmenu',function(e){e.preventDefault();});
        cv.addEventListener('wheel',function(e){e.preventDefault();zoom=Math.max(0.2,Math.min(8,zoom*(e.deltaY<0?1.1:0.9)));draw();},{passive:false});
        function resize(){
        W=cv.clientWidth||600;H=cv.clientHeight||452;
        if(cv.width!==W||cv.height!==H){cv.width=W;cv.height=H;}
        draw();
        }
        window.addEventListener('resize',resize);
        resize();
        }
        })();
        """;
}
