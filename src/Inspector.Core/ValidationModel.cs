
public sealed record JointAssignment(string Id, IReadOnlyList<bool> Restraints, string DiaphragmOption, string? Diaphragm);
public sealed record FrameAssignment(string Id, string GeometryType, string? MaterialOverwrite,
    IReadOnlyList<bool> StartReleases, IReadOnlyList<bool> EndReleases,
    IReadOnlyList<double> StartPartialFixity, IReadOnlyList<double> EndPartialFixity);
public sealed record AreaAssignment(string Id, string? Diaphragm, string? MaterialOverwrite);
public sealed record LinkConnection(string Id, string StartJoint, string EndJoint);
public sealed record LoadPattern(string Name, string Type, double SelfWeightMultiplier)
{
    // Null means this field was not read; blank on a Quake pattern means no auto-seismic load.
    public string? AutoSeismicCode { get; init; }
}
public sealed record CaseLoad(string Type, string Name, double Factor);
public sealed record LoadCase(string Name, string Type, int SubType, IReadOnlyList<CaseLoad>? Loads);
public sealed record CombinationTerm(string Type, string Name, int Mode, double Factor);
public sealed record LoadCombination(string Name, int Type, IReadOnlyList<CombinationTerm> Terms);
public sealed record MassLoad(string Pattern, double Factor);
public sealed record MassSource(string Name, bool IsDefault, bool IncludeElementMass, bool IncludeAddedMass,
    bool IncludeLoads, IReadOnlyList<MassLoad> Loads);
public sealed record Diaphragm(string Name, bool SemiRigid);

public interface IValidationSession : IModelSession
{
    DataSet<JointAssignment> ReadJointAssignments();
    DataSet<FrameAssignment> ReadFrameAssignments();
    DataSet<AreaAssignment> ReadAreaAssignments();
    DataSet<LinkConnection> ReadLinks();
    DataSet<LoadPattern> ReadLoadPatterns();
    DataSet<LoadCase> ReadLoadCases();
    DataSet<LoadCombination> ReadCombinations();
    DataSet<MassSource> ReadMassSources();
    DataSet<Diaphragm> ReadDiaphragms();
}

// Analysis is a separate opt-in capability. Ordinary inspection does not call it.
public enum AnalysisCaseState { NotRun = 1, CouldNotStart = 2, NotFinished = 3, Finished = 4, Unknown = 0 }
public sealed record AnalysisCaseStatus(string Name, AnalysisCaseState State, int RawStatus);
public sealed record AnalysisStatusData(DataSet<AnalysisCaseStatus> Cases);
public sealed record StoryDriftResult(string Story, string Case, string StepType, double StepNumber,
    string Direction, double Drift, string Label, double X, double Y, double Z)
{
    // X/Y/Z are the reported point location on ETABS 22.7.0, despite the CHM wording.
    public double? Displacement { get; init; }
    public double Uncertainty { get; init; }
}
public sealed record ModalParticipationResult(string Case, string StepType, double StepNumber, double Period,
    double UX, double UY, double UZ, double SumUX, double SumUY, double SumUZ,
    double RX, double RY, double RZ, double SumRX, double SumRY, double SumRZ);
public sealed record BaseReactionResult(string Case, string StepType, double StepNumber,
    double FX, double FY, double FZ, double MX, double MY, double MZ, Coordinate ReferencePoint)
{
    public double ForceUncertainty { get; init; }
    public double MomentUncertainty { get; init; }
    public double ReferenceUncertainty { get; init; }
    public string? RecoverySource { get; init; }
}
public sealed record AnalysisResultsData(DataSet<StoryDriftResult> StoryDrifts,
    DataSet<ModalParticipationResult> ModalParticipation, DataSet<BaseReactionResult> BaseReactions,
    IReadOnlyList<string> SelectedCases, IReadOnlyList<string> SelectedCombinations,
    IReadOnlyList<AnalysisTableSnapshot> ResultTables, IReadOnlyList<ExtractionIssue> Issues)
{
    public DataSet<StoryForceResult>? StoryForces { get; init; }
    public AnalysisVerificationEvidence? VerificationEvidence { get; init; }
    public ExtendedAnalysisEvidence? Extended { get; init; }
}
public sealed record AnalysisTableSnapshot(string Key, string Name, bool IsEmpty,
    IReadOnlyList<string> Fields, int Rows);
public sealed record ResponseSpectrumLoad(string Direction, string Function, double ScaleFactor,
    string CoordinateSystem, double Angle);
public sealed record ResponseSpectrumCase(string Name, string ModalCase, int ModalCombinationType,
    double F1, double F2, int PeriodicRigidCombinationType, double Td,
    int DirectionCombinationType, double DirectionCombinationValue, IReadOnlyList<ResponseSpectrumLoad> Loads);
public sealed record GeometricNonlinearitySetting(string Case, string CaseType, int RawOption, string? Meaning);
public sealed record AnalysisDefinitionData(DataSet<ResponseSpectrumCase> ResponseSpectrumCases,
    DataSet<GeometricNonlinearitySetting> GeometricNonlinearity);
// Read-only analysis context. Any component may be null when its extraction failed; issues are retained explicitly.
public sealed record AnalysisInput(AnalysisStatusData? Status, AnalysisResultsData? Results,
    AnalysisDefinitionData? Definitions, IReadOnlyList<ExtractionIssue> Issues);

public interface IAnalysisSession
{
    void RunAnalysis();
    AnalysisStatusData ReadAnalysisStatus();
    AnalysisResultsData ReadSelectedAnalysisResults();
    AnalysisDefinitionData ReadAnalysisDefinitions();
}

public enum AnalysisAuthorization { NotGranted, Granted }

public sealed record ValidationData(DataSet<JointAssignment>? JointAssignments, DataSet<FrameAssignment>? FrameAssignments,
    DataSet<AreaAssignment>? AreaAssignments, DataSet<LinkConnection>? Links, DataSet<LoadPattern>? LoadPatterns,
    DataSet<LoadCase>? LoadCases, DataSet<LoadCombination>? Combinations, DataSet<MassSource>? MassSources,
    DataSet<Diaphragm>? Diaphragms, IReadOnlyList<ExtractionIssue> Issues)
{
    public DataSet<MaterialTakeoffResult>? MaterialTakeoffs { get; init; }
    public string ContentFingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        JointAssignments = JointAssignments?.Items, FrameAssignments = FrameAssignments?.Items,
        AreaAssignments = AreaAssignments?.Items, Links = Links?.Items, LoadPatterns = LoadPatterns?.Items,
        LoadCases = LoadCases?.Items, Combinations = Combinations?.Items, MassSources = MassSources?.Items,
        Diaphragms = Diaphragms?.Items, MaterialTakeoffs = MaterialTakeoffs?.Items
    })));
}

// Analysis is optional: definition-only inspection runs without it, and Phase 3 checks depend on it.
public sealed record InspectionInput(ModelData Model, ValidationData Validation, AnalysisInput? Analysis = null)
{
    public ConcreteDesignStatusData? ConcreteDesignStatus { get; init; }
    public DataSet<ConcreteColumnDesignResult>? ConcreteColumns { get; init; }
    public DataSet<ConcreteBeamDesignResult>? ConcreteBeams { get; init; }
    public DataSet<ConcretePierDesignResult>? ConcretePiers { get; init; }
    public IReadOnlyList<ProvidedRebar>? ProvidedRebar { get; init; }
    public RebarEstimate? RebarEstimate { get; init; }
    public DataSet<MaterialTakeoffResult>? MaterialTakeoffs { get; init; }
}

public sealed record ConcreteDesignMember(string FrameId, string Orientation, int Procedure);
public sealed record ConcreteDesignStatusData(bool? ResultsAvailable, DataSet<ConcreteDesignMember>? Members,
    IReadOnlyList<ExtractionIssue> Issues);

// Optional capability so older/read-only adapters can explicitly report unknown state.
public interface IConcreteDesignStateSession
{
    ConcreteDesignStatusData ReadConcreteDesignStatus();
}


