
public sealed class InspectionExtractor(ModelExtractor modelExtractor, ILogger<InspectionExtractor> logger)
{
    public InspectionInput Extract(IValidationSession session, CancellationToken cancellation = default,
        Action<string>? progress = null, bool readAnalysis = false, bool readDesign = false, ConfigurationInput? configuration = null)
    {
        var model = modelExtractor.Extract(session, cancellation, progress);
        var first = ReadOnce();
        progress?.Invoke("Phase 2 consistency verification");
        var repeatedModel = modelExtractor.Extract(session, cancellation);
        var second = ReadOnce();
        if (ModelConsistency.ContentFingerprint(model) != ModelConsistency.ContentFingerprint(repeatedModel) ||
            first.ContentFingerprint() != second.ContentFingerprint())
            throw new ModelChangedException("Geometry or validation definitions changed between repeated reads. Discarding the inspection.");
        var validation = first with { Issues = first.Issues.Concat(second.Issues).Distinct().ToArray() };
        var input = new InspectionInput(model, validation, readAnalysis ? ReadAnalysis() : null)
            { ProvidedRebar = configuration?.ProvidedRebar, MaterialTakeoffs = validation.MaterialTakeoffs };
        if (readDesign)
        {
            ConcreteDesignStatusData designStatus;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke("ConcreteDesignStatus");
                designStatus = session is IConcreteDesignStateSession stateSession
                    ? stateSession.ReadConcreteDesignStatus()
                    : new(null, null, [new("ConcreteDesignStatus", "Design state is not available from this connection.", null, null)]);
            }
            catch (ApiException ex)
            {
                designStatus = new(null, null, [new("ConcreteDesignStatus", ex.Message, ex.Operation, ex.ReturnCode)]);
            }
            input = input with { ConcreteDesignStatus = designStatus };
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke("ConcreteColumnDesign");
            DataSet<ConcreteColumnDesignResult> columns;
            try
            {
                columns = designStatus.ResultsAvailable == false ? new([], [], []) : session is IConcreteDesignSession designSession
                    ? designSession.ReadConcreteColumnDesign()
                    : new([], [], ["The connected session does not support concrete column design extraction."]) { ReadSucceeded = false };
            }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Concrete column design extraction failed");
                columns = new([], [], [ex.Message]) { ReadSucceeded = false };
            }
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed while reading concrete column design results.");
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke("ConcreteBeamDesign");
            DataSet<ConcreteBeamDesignResult> beams;
            try
            {
                beams = designStatus.ResultsAvailable == false ? new([], [], []) : session is IConcreteDesignSession beamSession
                    ? beamSession.ReadConcreteBeamDesign()
                    : new([], [], ["The connected session does not support concrete beam design extraction."]) { ReadSucceeded = false };
            }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Concrete beam design extraction failed");
                beams = new([], [], [ex.Message]) { ReadSucceeded = false };
            }
            cancellation.ThrowIfCancellationRequested();
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed while reading concrete beam design results.");
            progress?.Invoke("ConcretePierDesign");
            DataSet<ConcretePierDesignResult> piers;
            try
            {
                piers = session is IConcreteDesignSession pierSession
                    ? pierSession.ReadConcretePierDesign()
                    : new([], [], ["The connected session does not support concrete pier design extraction."]) { ReadSucceeded = false };
            }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Concrete pier design extraction failed");
                piers = new([], [], [ex.Message]) { ReadSucceeded = false };
            }
            cancellation.ThrowIfCancellationRequested();
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed while reading concrete pier design results.");
            if (session is IConcreteDesignStateSession repeatedStateSession)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var repeated = repeatedStateSession.ReadConcreteDesignStatus();
                    if (designStatus.ResultsAvailable is not null && repeated.ResultsAvailable is not null && designStatus.ResultsAvailable != repeated.ResultsAvailable ||
                        designStatus.Members is not null && repeated.Members is not null &&
                        JsonSerializer.Serialize(designStatus.Members.Items) != JsonSerializer.Serialize(repeated.Members.Items))
                        throw new ModelChangedException("Concrete design state changed during inspection. Discarding the inspection.");
                    designStatus = designStatus with { Issues = designStatus.Issues.Concat(repeated.Issues).Distinct().ToArray() };
                }
                catch (ApiException ex)
                {
                    designStatus = designStatus with { Issues = [..designStatus.Issues, new("ConcreteDesignStatusVerification", ex.Message, ex.Operation, ex.ReturnCode)] };
                }
            }
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed while verifying design state.");
            input = input with { ConcreteDesignStatus = designStatus, ConcreteColumns = columns, ConcreteBeams = beams, ConcretePiers = piers };
        }
        return input with { RebarEstimate = RebarEstimator.Estimate(input, configuration?.Settings.RebarEstimation ?? new()) };

        // Analysis reads are isolated per component so one failed result call never discards the others.
        AnalysisInput ReadAnalysis()
        {
            if (session is not IAnalysisSession analysisSession)
                return new(null, null, null, [new("Analysis", "Analysis data is not available from this connection.", null, null)]);
            List<ExtractionIssue> issues = [];
            T? Read<T>(string component, Func<T> read) where T : class
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Invoke(component);
                try { return read(); }
                catch (ApiException ex)
                {
                    logger.LogError(ex, "Analysis extraction failed for {Component}", component);
                    issues.Add(new(component, ex.Message, ex.Operation, ex.ReturnCode));
                    return null;
                }
            }
            var status = Read("AnalysisStatus", analysisSession.ReadAnalysisStatus);
            var noAnalysis = ModelStateAssessment.Assess(new(model, validation, new(status, null, null, issues))).Analysis == OperationState.NotRun;
            var results = noAnalysis ? null : Read("AnalysisResults", analysisSession.ReadSelectedAnalysisResults);
            var definitions = Read("AnalysisDefinitions", analysisSession.ReadAnalysisDefinitions);
            var repeatedStatus = Read("AnalysisStatusVerification", analysisSession.ReadAnalysisStatus);
            if (status is not null && repeatedStatus is not null &&
                JsonSerializer.Serialize(status.Cases.Items) != JsonSerializer.Serialize(repeatedStatus.Cases.Items))
                throw new ModelChangedException("Analysis case state changed during inspection. Discarding the inspection.");
            cancellation.ThrowIfCancellationRequested();
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed while reading analysis results.");
            return new(status, results, definitions, issues);
        }

        ValidationData ReadOnce()
        {
            List<ExtractionIssue> issues = [];
            DataSet<T>? Read<T>(string component, Func<DataSet<T>> read)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                    throw new ModelChangedException("Model context changed while extracting Phase 2 inputs.");
                progress?.Invoke(component);
                try { return read(); }
                catch (ApiException ex)
                {
                    logger.LogError(ex, "Phase 2 extraction failed for {Component}", component);
                    issues.Add(new(component, ex.Message, ex.Operation, ex.ReturnCode));
                    return null;
                }
            }
            var joints = Read("JointAssignments", session.ReadJointAssignments);
            var frames = Read("FrameAssignments", session.ReadFrameAssignments);
            var areas = Read("AreaAssignments", session.ReadAreaAssignments);
            var links = Read("Links", session.ReadLinks);
            var patterns = Read("LoadPatterns", session.ReadLoadPatterns);
            var cases = Read("LoadCases", session.ReadLoadCases);
            var combos = Read("Combinations", session.ReadCombinations);
            var mass = Read("MassSources", session.ReadMassSources);
            var diaphragms = Read("Diaphragms", session.ReadDiaphragms);
            var takeoffs = Read("MaterialTakeoffs", session.ReadMaterialTakeoff);
            cancellation.ThrowIfCancellationRequested();
            if (!ModelConsistency.SameContext(model.Identity, session.ReadIdentity()))
                throw new ModelChangedException("Model context changed after Phase 2 extraction.");
            return new(joints, frames, areas, links, patterns, cases, combos, mass, diaphragms, issues)
                { MaterialTakeoffs = takeoffs };
        }
    }
}


