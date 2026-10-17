
public enum CheckStatus { PASS, FAIL, WARNING, NOT_VERIFIED, SKIPPED, NOT_APPLICABLE, ERROR }
public enum Severity { CRITICAL, HIGH, MEDIUM, LOW, INFORMATIONAL }
public enum VerificationState { NOT_IMPLEMENTED, IMPLEMENTED_UNVERIFIED, VERIFIED, DISABLED }
public enum FindingKind { Information, ModelProblem, MissingInput, IncompleteConfiguration, ApiUnavailable, OperationNotPerformed, ReviewRequired, MissingVerificationInput }
public enum CalculationValidation { Unverified, ResultConsistency, IndependentLoadBalance }
public sealed record LengthCriterion(double Value, string Unit);
public sealed record PatternExpectation(string Name, string? Type, double? SelfWeightMultiplier);
public sealed record GeometrySettings(LengthCriterion? CoincidentTolerance = null, LengthCriterion? ShortFrameThreshold = null,
    IReadOnlyList<string>? IntentionalCoincidentJoints = null, IReadOnlyList<string>? IntentionalDisconnectedElements = null,
    bool? RequireFloorDiaphragms = null, IReadOnlyList<string>? IntentionalUnreferencedJoints = null);
public sealed record LoadingSettings(IReadOnlyList<PatternExpectation>? ExpectedPatterns = null,
    IReadOnlyList<string>? RequiredCombinations = null);
public sealed record AnalysisSettings(double? DriftRatioLimit = null, IReadOnlyList<string>? DriftCases = null,
    IReadOnlyList<string>? DriftDirections = null, string? ModalCase = null,
    double? MinimumCumulativeUx = null, double? MinimumCumulativeUy = null,
    double? MaxStoryPointDisplacement = null, IReadOnlyList<string>? DisplacementCases = null,
    IReadOnlyList<string>? DisplacementDirections = null);
public sealed record EtabsSettings(string RequiredVersionPrefix = "22.7", string ConnectionMode = "AttachOnly");
public sealed record InspectionSettings(string SchemaVersion = "1.0", bool Confirmed = false,
    GeometrySettings? Geometry = null, LoadingSettings? Loading = null, AnalysisSettings? Analysis = null)
{
    public EtabsSettings? Etabs { get; init; }
    // Preserve the PRD's future-phase settings as data; no code-dependent conclusion uses them in Phase 2.
    public JsonElement? Project { get; init; }
    public JsonElement? Codes { get; init; }
    public JsonElement? Seismic { get; init; }
    public SeismicVerificationSettings? SeismicVerification { get; init; }
    public Phase3ReviewSettings? Phase3Review { get; init; }
    public JsonElement? Serviceability { get; init; }
    public JsonElement? Reinforcement { get; init; }
    public string? ReinforcementFile { get; init; }
    public string? ReinforcementAreaUnit { get; init; }
    public RebarEstimationSettings? RebarEstimation { get; init; }
    public JsonElement? Inspection { get; init; }
    public JsonElement? Reporting { get; init; }
    public JsonElement? QualityControl { get; init; }
}
public sealed record ConfigurationInput(InspectionSettings Settings, string? Path, string? Sha256, bool WasFound)
{
    public IReadOnlyList<ProvidedRebar>? ProvidedRebar { get; init; }
    public string? ReinforcementSourcePath { get; init; }
    public string? ReinforcementSha256 { get; init; }
    public IReadOnlyList<string> ReinforcementLimitations { get; init; } = [];
}
public sealed record Evidence(string Description, string Source, string? Value = null, string? Unit = null);
public sealed record Finding(string Id, string CheckId, string Module, string Title, CheckStatus Status, Severity Severity,
    string? ElementId, string? ElementType, string? Story, Coordinate? Coordinates, string? LoadCaseOrCombination,
    double? Actual, double? Limit, string? Unit, string? Standard, string? CodeClause, string RuleVersion,
    string CalculationMethod, IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> PossibleCauses,
    IReadOnlyList<string> Recommendations, string VerificationProcedure, DateTimeOffset Timestamp)
{
    public FindingKind Kind { get; init; } = FindingKind.Information;
    public CalculationValidation CalculationValidation { get; init; }
    public IReadOnlyList<string> AffectedElements { get; init; } = [];
    public IReadOnlyList<string> RelatedCheckIds { get; init; } = [];
}
public sealed record CheckDefinition(string Id, string Module, string Title, string Method,
    IReadOnlyList<string> RequiredData, VerificationState State = VerificationState.VERIFIED, string RuleVersion = "1.0");
public interface IInspectionCheck
{
    CheckDefinition Definition { get; }
    IReadOnlyList<Finding> Evaluate(InspectionContext context);
}
public sealed record InspectionContext(InspectionInput Input, ConfigurationInput Configuration, DateTimeOffset Timestamp)
{
    public ModelStateAssessment State => ModelStateAssessment.Assess(Input);
    public bool HasData(string key) => key switch
    {
        "ConcreteColumns" => Input.ConcreteColumns?.ReadSucceeded == true,
        "ConcreteBeams" => Input.ConcreteBeams?.ReadSucceeded == true,
        "ConcretePiers" => Input.ConcretePiers?.ReadSucceeded == true,
        "MaterialTakeoffs" => Input.MaterialTakeoffs?.ReadSucceeded == true,
        "Stories" => Input.Model.Stories is not null, "Joints" => Input.Model.Joints?.ReadSucceeded == true,
        "Frames" => Input.Model.Frames?.ReadSucceeded == true, "Areas" => Input.Model.Areas?.ReadSucceeded == true,
        "Materials" => Input.Model.Materials?.ReadSucceeded == true, "FrameSections" => Input.Model.FrameSections?.ReadSucceeded == true,
        "AreaSections" => Input.Model.AreaSections?.ReadSucceeded == true, "JointAssignments" => Input.Validation.JointAssignments?.ReadSucceeded == true,
        "FrameAssignments" => Input.Validation.FrameAssignments?.ReadSucceeded == true, "AreaAssignments" => Input.Validation.AreaAssignments?.ReadSucceeded == true,
        "Links" => Input.Validation.Links?.ReadSucceeded == true, "LoadPatterns" => Input.Validation.LoadPatterns?.ReadSucceeded == true,
        "LoadCases" => Input.Validation.LoadCases?.ReadSucceeded == true, "Combinations" => Input.Validation.Combinations?.ReadSucceeded == true,
        "MassSources" => Input.Validation.MassSources?.ReadSucceeded == true, "Diaphragms" => Input.Validation.Diaphragms?.ReadSucceeded == true,
        "AnalysisStatus" => Input.Analysis?.Status is not null, "AnalysisResults" => Input.Analysis?.Results is not null,
        "AnalysisDefinitions" => Input.Analysis?.Definitions is not null,
        "Modifiers" => Input.Analysis?.Results?.Extended is not null,
        _ => false
    };
}
public sealed record CheckCoverage(string CheckId, VerificationState Implementation, int Findings)
{
    public string? DeferredReason { get; init; }
}
public sealed record InspectionReport(string SchemaVersion, string Module, InspectionInput Input, ConfigurationInput Configuration,
    IReadOnlyList<Finding> Findings, IReadOnlyList<CheckCoverage> Coverage, DateTimeOffset Timestamp)
{
    public ModelStateAssessment ModelState => ModelStateAssessment.Assess(Input);
    public bool HasFailures => Findings.Any(f => f.Status == CheckStatus.FAIL);
    public bool IsComplete => !Findings.Any(f => f.Status is CheckStatus.ERROR or CheckStatus.SKIPPED or CheckStatus.NOT_VERIFIED) &&
        Coverage.All(c => c.Implementation == VerificationState.VERIFIED && c.DeferredReason is null) && Input.Model.IsComplete && Input.Validation.Issues.Count == 0;
    public ExitCode ExitCode => HasFailures ? global::ExitCode.EngineeringFailures : IsComplete ? global::ExitCode.Success : global::ExitCode.Incomplete;
}


public sealed record Observation(CheckStatus Status, string Message, string? ElementId = null, string? ElementType = null,
    string? Story = null, Coordinate? Position = null, double? Actual = null, double? Limit = null,
    string? Unit = null, IReadOnlyList<Evidence>? Evidence = null, string? Combination = null)
{
    public FindingKind? Kind { get; init; }
    public CalculationValidation CalculationValidation { get; init; }
}


