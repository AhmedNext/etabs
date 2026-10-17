
public static class Program
{
    public static IReadOnlyList<string> StoryElementIds(ModelData model, string storyId)
    {
        if (model.Stories is null) throw new ApiException("Story navigation", null, "Story definitions are unavailable.");
        if (!model.Stories.Stories.Any(s => s.Name == storyId))
            throw new ConfigurationException($"Story '{storyId}' was not found in the active model.");
        if (model.Frames is null || model.Areas is null)
            throw new ApiException("Story navigation", null, "Frame and area inventories are required for complete story selection.");
        return model.Frames.Items.Where(f => f.Story == storyId).Select(f => "Frame:" + f.Id)
            .Concat(model.Areas.Items.Where(a => a.Story == storyId).Select(a => "Area:" + a.Id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> FailedElementIds(InspectionReport report)
    {
        var frames = report.Input.Model.Frames?.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var areas = report.Input.Model.Areas?.Items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        HashSet<string> targets = new(StringComparer.Ordinal);
        foreach (var finding in report.Findings.Where(f => f.Status == CheckStatus.FAIL))
        {
            if (finding.ElementType == "FrameGroup")
            {
                foreach (var member in finding.AffectedElements)
                    if (frames.Contains(member)) targets.Add("Frame:" + member);
                continue;
            }
            var id = finding.ElementId;
            if (string.IsNullOrWhiteSpace(id) || id.Equals("model", StringComparison.OrdinalIgnoreCase)) continue;
            if (finding.ElementType is "Frame" or "Area")
            {
                var prefix = finding.ElementType + ":";
                var names = finding.ElementType == "Frame" ? frames : areas;
                if (names.Contains(id)) targets.Add(prefix + id);
                else if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && names.Contains(id[prefix.Length..]))
                    targets.Add(prefix + id[prefix.Length..]);
            }
            else if (finding.ElementType is null)
            {
                if (id.StartsWith("Area:", StringComparison.OrdinalIgnoreCase) && areas.Contains(id[5..])) targets.Add("Area:" + id[5..]);
                else if (id.StartsWith("Frame:", StringComparison.OrdinalIgnoreCase) && frames.Contains(id[6..])) targets.Add("Frame:" + id[6..]);
                else if (frames.Contains(id) && !areas.Contains(id)) targets.Add("Frame:" + id);
                else if (areas.Contains(id) && !frames.Contains(id)) targets.Add("Area:" + id);
            }
        }
        return targets.Order(StringComparer.Ordinal).ToArray();
    }

    public static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try { options = CliOptions.Parse(args); }
        catch (ConfigurationException ex) { Console.Error.WriteLine(ex.Message); return (int)ExitCode.ConfigurationError; }
        if (options.Command == "help")
        {
            Console.WriteLine("ETABS Model Inspector - ETABS 22.7.0, attach only");
            Console.WriteLine("  connect                         Verify the running instance and show model identity");
            Console.WriteLine("  init-config [--config FILE.json] Generate model configuration and a starter reinforcement.json; existing files are backed up in trash");
            Console.WriteLine("  extract [--details]             Read the entire model's Phase 1 data");
            Console.WriteLine("          [--evidence FILE.json]   Write local extraction evidence (no offline import)");
            Console.WriteLine("  inspect all|geometry|loads|analysis|seismic|design|sanity|audit");
            Console.WriteLine("                                   Run implemented checks on the live model; analysis/seismic read result data");
            Console.WriteLine("          [--config FILE.json]     Project criteria; defaults to config/project.asce7-16.json");
            Console.WriteLine("          [--details] [--evidence FILE.json]");
            Console.WriteLine("  locate failed [--config FILE]   Inspect all and select failing frame/area objects in ETABS");
            Console.WriteLine("  locate ELEMENT_ID               Select a frame ID, Frame:ID or Area:ID in ETABS");
            Console.WriteLine("  locate story STORY_ID           Select all frames and areas assigned to a story");
            Console.WriteLine("  report --format html            Inspect all and write a standalone HTML dashboard");
            Console.WriteLine("         [--submission]            Add draft municipal sign-off and compliance matrix");
            Console.WriteLine("         [--output FILE.html]      Defaults to docs/reports; --config FILE is supported");
            Console.WriteLine("         [--result-tables FILE.json] Recover unavailable engineering result tables from a model-bound bundle");
            Console.WriteLine("         Open complete ETABS result grids are also read automatically, without clicking or changing selections.");
            Console.WriteLine("  analysis-status                 Read ETABS status for all analysis cases (read only)");
            Console.WriteLine("  analysis-results                Read results using the existing ETABS output selection");
            Console.WriteLine("  --help                          Show this help");
            Console.WriteLine("Exit codes: 0 success, 1 verified criterion failures, 2 incomplete, 3 usage/configuration,");
            Console.WriteLine("            4 connection, 5 API/technical failure, 130 cancelled.");
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var services = new ServiceCollection();
            var logPath = Path.Combine(ProjectPaths.Root, "trash", "logs", $"inspector_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.jsonl");
            using var fileLogger = new FileLoggerProvider(logPath);
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug)
                .AddProvider(fileLogger));
            services.AddTransient<ModelExtractor>();
            services.AddTransient<InspectionExtractor>();
            services.AddTransient<InspectionEngine>();
            services.AddTransient<SmartAnalysisController>();
            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Inspector");
            try
            {
                var configuration = options.Command is "inspect" or "report" ||
                    options.Command == "locate" && !options.LocateStory && options.LocateTarget == "failed"
                    ? ProjectConfiguration.Load(options.ConfigurationPath, options.ExplicitConfiguration) : null;
                return await StaRunner.Run(() =>
                {
                    using var session = EtabsSession.Attach(logger, cancellation.Token);
                    if (options.Command == "connect")
                    {
                        ShowIdentity(session.ReadIdentity());
                        return (int)ExitCode.Success;
                    }
                    if (options.Command == "init-config")
                    {
                        var input = provider.GetRequiredService<InspectionExtractor>().Extract(session, cancellation.Token,
                            readAnalysis: true, readDesign: true);
                        var baseline = BaselineConfigurationGenerator.Generate(input);
                        if (!ModelConsistency.SameContext(input.Model.Identity, session.ReadIdentity()))
                            throw new ModelChangedException("Model context changed before generating configuration.");
                        var backup = Path.Combine(ProjectPaths.Root, "trash", "config-backups", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N"));
                        var paths = BaselineConfigurationGenerator.Write(baseline, options.ConfigurationPath, backup);
                        foreach (var path in paths) Console.WriteLine($"Generated: {path}");
                        if (Directory.Exists(backup)) Console.WriteLine($"Previous configuration backup: {backup}");
                        Console.WriteLine($"Starter reinforcement entries: {baseline.Schedule.Count}. Generated bars require engineering approval before DETAIL-008 can verify provided reinforcement.");
                        foreach (var note in baseline.Notes) Console.WriteLine(note);
                        return (int)ExitCode.Success;
                    }
                    if (options.Command == "analysis-status")
                    {
                        ShowIdentity(session.ReadIdentity());
                        return ShowAnalysisStatus(session.ReadAnalysisStatus());
                    }
                    if (options.Command == "analysis-results")
                    {
                        var before = session.ReadIdentity();
                        ShowIdentity(before);
                        var result = session.ReadSelectedAnalysisResults();
                        var after = session.ReadIdentity();
                        if (!ModelConsistency.SameContext(before, after))
                            throw new ModelChangedException("Model context changed while reading analysis results.");
                        Console.WriteLine($"Selected cases: {string.Join(", ", result.SelectedCases)}");
                        Console.WriteLine($"Selected combinations: {string.Join(", ", result.SelectedCombinations)}");
                        Console.WriteLine($"Story drift rows: {result.StoryDrifts.Items.Count}; modal rows: {result.ModalParticipation.Items.Count}; base reaction rows: {result.BaseReactions.Items.Count}.");
                        foreach (var table in result.ResultTables)
                            Console.WriteLine($"Result table {table.Name}: {(table.IsEmpty ? "empty" : $"{table.Rows} rows")}{(table.Fields.Count == 0 ? "" : $"; fields={string.Join(",", table.Fields)}")}");
                        if (options.Details)
                            foreach (var row in result.ModalParticipation.Items)
                                Console.WriteLine($"Modal {row.Case} mode {row.StepNumber:G6}: T={row.Period:G6}s; cumulative UX={row.SumUX:G6}, UY={row.SumUY:G6}, UZ={row.SumUZ:G6}.");
                        Console.WriteLine("Results use ETABS display units. No freshness or code compliance conclusion is made.");
                        foreach (var limitation in result.StoryDrifts.Limitations) Console.WriteLine($"NOT VERIFIED: {limitation}");
                        foreach (var issue in result.Issues) Console.WriteLine($"ERROR [{issue.Component}] {issue.Message}");
                        if (options.EvidencePath is not null)
                            WriteEvidence(options.EvidencePath, new { schemaVersion = "phase3-analysis-results-1", identity = before,
                                result, capabilities = CapabilityRegistry.All });
                        if (result.Issues.Count > 0) return (int)ExitCode.ApiFailure;
                        return result.StoryDrifts.Items.Count + result.ModalParticipation.Items.Count + result.BaseReactions.Items.Count == 0
                            ? (int)ExitCode.Incomplete : (int)ExitCode.Success;
                    }
                    if (options.Command == "locate")
                    {
                        if (options.LocateStory)
                        {
                            var model = provider.GetRequiredService<ModelExtractor>().Extract(session, cancellation.Token);
                            var storyIds = StoryElementIds(model, options.LocateTarget!);
                            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                                throw new ModelChangedException("Model context changed before locating story elements.");
                            session.SelectElements(storyIds);
                            Console.WriteLine($"Selected {storyIds.Count} elements on story {options.LocateTarget} in ETABS.");
                            return (int)ExitCode.Success;
                        }
                        if (options.LocateTarget != "failed")
                        {
                            session.SelectElements([options.LocateTarget!]);
                            Console.WriteLine($"Selected element {options.LocateTarget} in ETABS.");
                            return (int)ExitCode.Success;
                        }
                        var input = provider.GetRequiredService<InspectionExtractor>().Extract(session, cancellation.Token,
                            readAnalysis: true, readDesign: true, configuration: configuration);
                        var report = provider.GetRequiredService<InspectionEngine>().Run(input, configuration!, "all",
                            CheckCatalog.Implemented, CheckCatalog.All, cancellation.Token);
                        var ids = FailedElementIds(report);
                        if (!ModelConsistency.SameContext(input.Model.Identity, session.ReadIdentity()))
                            throw new ModelChangedException("Model context changed before locating failed elements.");
                        session.SelectElements(ids);
                        Console.WriteLine($"Selected {ids.Count} failing elements in the ETABS window.");
                        return (int)ExitCode.Success;
                    }
                    if (options.Command == "report")
                    {
                        var input = provider.GetRequiredService<InspectionExtractor>().Extract(session, cancellation.Token,
                            step => Console.WriteLine($"Reading {step}..."), readAnalysis: true, readDesign: true, configuration: configuration);
                        if(options.ResultTablesPath is not null)input=ResultTableRecovery.Apply(input,ResultTableRecovery.Load(options.ResultTablesPath));
                        var report = DiagnosisEngine.Enrich(provider.GetRequiredService<InspectionEngine>().Run(input, configuration!, "all",
                            CheckCatalog.Implemented, CheckCatalog.All, cancellation.Token));
                        var path = Path.GetFullPath(options.OutputPath ?? Path.Combine(ProjectPaths.Root, "docs", "reports",
                            $"inspection_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.html"));
                        HtmlReportGenerator.GenerateReport(report, path, options.Submission);
                        var plansDirectory = Path.Combine(Path.GetDirectoryName(path)!, "plans");
                        SvgPlanGenerator.GeneratePlans(report, plansDirectory);
                        Console.WriteLine($"HTML Report generated: {path}");
                        Console.WriteLine($"SVG floor plans generated: {plansDirectory}");
                        ShowRebarEstimate(input.RebarEstimate);
                        return (int)report.ExitCode;
                    }
                    if (options.Command == "inspect")
                    {
                        var input = provider.GetRequiredService<InspectionExtractor>().Extract(session, cancellation.Token,
                            step => Console.WriteLine($"Reading {step}..."), options.Module is "all" or "analysis" or "seismic" or "sanity" or "audit", options.Module is "all" or "design" or "sanity", configuration);
                        if(options.ResultTablesPath is not null)input=ResultTableRecovery.Apply(input,ResultTableRecovery.Load(options.ResultTablesPath));
                        var report = DiagnosisEngine.Enrich(provider.GetRequiredService<InspectionEngine>().Run(input, configuration!, options.Module,
                            CheckCatalog.Implemented, CheckCatalog.All, cancellation.Token));
                        ShowIdentity(input.Model.Identity);
                        var results = new Table().AddColumn("Check").AddColumn("Status").AddColumn("Object").AddColumn("Finding");
                        foreach (var f in report.Findings) results.AddRow(new Text(f.CheckId), new Text(f.Status.ToString()), new Text(f.ElementId ?? "model"), new Text(f.Title));
                        AnsiConsole.Write(results);
                        if (options.Details)
                            foreach (var f in report.Findings)
                            {
                                Console.WriteLine($"{f.CheckId} [{f.Id}]: {f.CalculationMethod}");
                                foreach (var e in f.Evidence) Console.WriteLine($"  {e.Description}: {e.Value} ({e.Source})");
                                foreach (var action in f.Recommendations) Console.WriteLine($"  Review: {action}");
                            }
                        Console.WriteLine($"Implemented numerical/screening rules: {report.Coverage.Count(c => c.Implementation == VerificationState.VERIFIED)}/{report.Coverage.Count}; findings: {report.Findings.Count}; complete coverage: {report.IsComplete}.");
                        Console.WriteLine($"Not implemented in the selected catalog: {string.Join(", ", report.Coverage.Where(c => c.Implementation == VerificationState.NOT_IMPLEMENTED).Select(c => c.CheckId))}");
                        Console.WriteLine("PASS applies only to the stated numerical criterion. Missing evidence and unresolved engineering scope remain NOT VERIFIED; implementation coverage does not establish model compliance.");
                        ShowRebarEstimate(input.RebarEstimate);
                        if (options.EvidencePath is not null) WriteEvidence(options.EvidencePath,
                            report.Module is "analysis" or "seismic"
                                ? CreateAnalysisEvidenceEnvelope(report)
                                : new { report, capabilities = CapabilityRegistry.All });
                        Console.WriteLine($"Log: {Path.GetFullPath(logPath)}");
                        return (int)report.ExitCode;
                    }
                    var data = provider.GetRequiredService<ModelExtractor>().Extract(session, cancellation.Token,
                        step => Console.WriteLine($"Reading {step.ToLowerInvariant()}..."));
                    ShowIdentity(data.Identity);
                    ShowData(data, options.Details);
                    if (options.EvidencePath is not null)
                    {
                        WriteEvidence(options.EvidencePath, new { schemaVersion = "phase1-extraction-1", model = data,
                            contentFingerprint = ModelConsistency.ContentFingerprint(data), capabilities = CapabilityRegistry.All });
                    }
                    Console.WriteLine($"Log: {Path.GetFullPath(logPath)}");
                    return data.IsComplete ? (int)ExitCode.Success : (int)ExitCode.Incomplete;
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command {Command} failed", options.Command);
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine($"Log: {Path.GetFullPath(logPath)}");
                return ex switch
                {
                    OperationCanceledException => (int)ExitCode.Cancelled,
                    ConnectionException => (int)ExitCode.ConnectionFailure,
                    ConfigurationException => (int)ExitCode.ConfigurationError,
                    ModelChangedException => (int)ExitCode.Incomplete,
                    _ => (int)ExitCode.ApiFailure
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot initialize output/logging: {ex.Message}");
            return (int)ExitCode.ApiFailure;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static void ShowRebarEstimate(RebarEstimate? estimate)
    {
        if (estimate is { Items.Count: > 0 })
            Console.WriteLine(FormattableString.Invariant($"Estimated rebar: {estimate.BaseSteelKg / 1000:F2} t base; {estimate.LowSteelKg / 1000:F2}–{estimate.HighSteelKg / 1000:F2} t range from {estimate.TotalConcreteM3:F3} m³ modeled concrete. Estimating assumptions, not a provided-bar schedule."));
        else Console.WriteLine("Estimated rebar: unavailable; usable concrete quantities are required.");
    }

    private static void WriteEvidence<T>(string evidencePath, T data)
    {
        var path = Path.IsPathRooted(evidencePath)
            ? Path.GetFullPath(evidencePath)
            : Path.Combine(ProjectPaths.Root, "docs", "reports", Path.GetFileName(evidencePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, data, new JsonSerializerOptions { WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Console.WriteLine($"Evidence: {path}");
    }

    private static object CreateAnalysisEvidenceEnvelope(InspectionReport report)
    {
        var input = report.Input;
        var analysis = input.Analysis;
        object? analysisSummary = null;
        if (analysis is not null)
        {
            var results = analysis.Results;
            var extended = results?.Extended;
            analysisSummary = new
            {
                analysis.Status,
                analysis.Definitions,
                analysis.Issues,
                Results = results is null ? null : new
                {
                    results.SelectedCases,
                    results.SelectedCombinations,
                    StoryDriftRows = results.StoryDrifts.Items.Count,
                    ModalParticipationRows = results.ModalParticipation.Items.Count,
                    BaseReactionRows = results.BaseReactions.Items.Count,
                    StoryForceRows = results.StoryForces?.Items.Count,
                    results.ResultTables,
                    results.Issues,
                    results.VerificationEvidence,
                    Extended = extended is null ? null : new
                    {
                        Tables = extended.Tables.Select(t => new { t.Key, t.Fields, RowCount = t.Rows.Count, t.UnavailableReason }),
                        extended.Modifiers,
                        ModeShapeRowCount = extended.ModeShapes.Count,
                        FrameForceRowCount = extended.FrameForces.Count,
                        extended.ConcreteCode,
                        extended.ConcreteResultsAvailable,
                        extended.Issues
                    }
                }
            };
        }
        return new
        {
            report = new
            {
                report.SchemaVersion,
                report.Module,
                Model = input.Model,
                Validation = input.Validation,
                Analysis = analysisSummary,
                report.Configuration,
                report.Findings,
                report.Coverage,
                report.Timestamp
            },
            capabilities = CapabilityRegistry.All
        };
    }

    private static void ShowIdentity(ModelIdentity identity)
    {
        Console.WriteLine($"ETABS {identity.EtabsVersion} | Model: {identity.FilePath}");
        Console.WriteLine($"Display: {identity.DisplayUnits.Force}, {identity.DisplayUnits.Length}, {identity.DisplayUnits.Temperature} | Locked: {identity.IsLocked}");
    }
    private static int ShowAnalysisStatus(AnalysisStatusData data)
    {
        var table = new Table().AddColumn("Analysis case").AddColumn("ETABS status");
        foreach (var item in data.Cases.Items)
            table.AddRow(new Text(item.Name), new Text(item.State == AnalysisCaseState.Unknown
                ? $"Unknown ({item.RawStatus})" : item.State.ToString()));
        AnsiConsole.Write(table);
        foreach (var limitation in data.Cases.Limitations) Console.WriteLine($"NOT VERIFIED: {limitation}");
        if (data.Cases.Items.Count == 0 || data.Cases.Items.Any(c => c.State != AnalysisCaseState.Finished))
            return (int)ExitCode.Incomplete;
        Console.WriteLine("ETABS reports all analysis cases finished. Result freshness and engineering validity are not established by case status alone.");
        return (int)ExitCode.Success;
    }
    private static void ShowData(ModelData data, bool details)
    {
        var table = new Table().AddColumn("Model data").AddColumn("Count");
        void Row(string name, int? count) => table.AddRow(new Text(name), new Text(count?.ToString() ?? "UNAVAILABLE"));
        Row("Stories (excluding base)", data.Stories?.Stories.Count);
        Row("Joints", data.Joints?.Items.Count); Row("Frames", data.Frames?.Items.Count);
        Row("Areas (including openings/null areas)", data.Areas?.Items.Count);
        Row("Materials", data.Materials?.Items.Count); Row("Frame section definitions", data.FrameSections?.Items.Count);
        Row("Area section definitions", data.AreaSections?.Items.Count);
        AnsiConsole.Write(table);
        foreach (var issue in data.Issues) Console.WriteLine($"ERROR [{issue.Component}] {issue.Message}");
        IEnumerable<string> limits = (data.Materials?.Limitations ?? []).Concat(data.FrameSections?.Limitations ?? [])
            .Concat(data.AreaSections?.Limitations ?? []);
        foreach (var limitation in limits) Console.WriteLine($"NOT EXTRACTED: {limitation}");
        if (details)
        {
            foreach (var story in data.Stories?.Stories ?? [])
                Console.WriteLine($"Story {story.Name}: elevation {story.Elevation:G8}, height {story.Height:G8} {data.Identity.DisplayUnits.Length}");
            foreach (var section in data.FrameSections?.Items ?? [])
                Console.WriteLine($"Frame section {section.Name}: {section.Shape}, {section.Material ?? "unknown material"}; depth={section.Depth?.ToString("G8") ?? "n/a"}, width={section.Width?.ToString("G8") ?? "n/a"}, diameter={section.Diameter?.ToString("G8") ?? "n/a"} {data.Identity.DisplayUnits.Length}");
            foreach (var section in data.AreaSections?.Items ?? [])
                Console.WriteLine($"Area section {section.Name}: {section.Kind}, {section.Material ?? "unknown material"}; membrane thickness={section.MembraneThickness?.ToString("G8") ?? "n/a"} {data.Identity.DisplayUnits.Length}");
            foreach (var material in data.Materials?.Items ?? [])
                Console.WriteLine($"Material {material.Name}: {material.Type}; E={material.ElasticModulus?.ToString("G8") ?? "n/a"}, fc={material.ConcreteStrength?.ToString("G8") ?? "n/a"}, fy={material.RebarYieldStrength?.ToString("G8") ?? "n/a"} {data.Identity.DisplayUnits.Stress}");
        }
        Console.WriteLine($"Extraction {(data.IsComplete ? "complete" : "partial; see unavailable data above")}; {data.Duration.TotalSeconds:F2} s.");
        Console.WriteLine("Phase 1 extracts model definitions only. No engineering compliance checks have run.");
    }
}


