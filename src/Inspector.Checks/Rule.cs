
public sealed class Rule(CheckDefinition definition, Func<InspectionContext, IReadOnlyList<Observation>> evaluate) : IInspectionCheck
{
    public CheckDefinition Definition => definition;
    public IReadOnlyList<Finding> Evaluate(InspectionContext context)
    {
        var modelHash = new Evidence("Geometry/material content SHA-256", "report.input.model", ModelConsistency.ContentFingerprint(context.Input.Model));
        var validationHash = new Evidence("Validation content SHA-256", "report.input.validation", context.Input.Validation.ContentFingerprint());
        return evaluate(context).Select(o =>
        {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{definition.Id}|{o.ElementId}|{o.Message}|{o.Combination}")))[..20];
        var source = new Evidence("Input model", "ETABS live model", context.Input.Model.Identity.FilePath);
        var criterion = new Evidence("Rule criterion", "Inspector verified data/screening rule " + definition.Id, definition.Method);
        var inputs = new Evidence("Required source datasets", "report.input + each dataset's API source records", string.Join(", ", definition.RequiredData));
        var frame = o.ElementType == "Frame" ? LookupFrame(context, o.ElementId) : null;
        return new Finding(id, definition.Id, definition.Module, o.Message, o.Status,
            o.Status switch { CheckStatus.FAIL => Severity.HIGH, CheckStatus.ERROR => Severity.HIGH,
                CheckStatus.WARNING => Severity.MEDIUM, _ => Severity.INFORMATIONAL },
            o.ElementId, o.ElementType, o.Story ?? frame?.Story, o.Position ?? frame?.Start, o.Combination, o.Actual, o.Limit, o.Unit,
            null, null, definition.RuleVersion, definition.Method, [source, criterion, inputs, modelHash, validationHash, ..o.Evidence ?? []],
            [], [], "Review the cited ETABS definition and rerun this rule after engineer-approved changes.", context.Timestamp)
        {
            CalculationValidation = o.CalculationValidation,
            Kind = o.Kind ?? o.Status switch
            {
                CheckStatus.FAIL when definition.Id is "LOAD-001" or "LOAD-007" => FindingKind.MissingInput,
                CheckStatus.FAIL => FindingKind.ModelProblem,
                CheckStatus.WARNING => FindingKind.ReviewRequired,
                CheckStatus.ERROR or CheckStatus.SKIPPED => FindingKind.ApiUnavailable,
                CheckStatus.NOT_VERIFIED when definition.Id is "CFG-001" or "LOAD-001" or "LOAD-006" or "LOAD-007" => FindingKind.IncompleteConfiguration,
                CheckStatus.NOT_VERIFIED => FindingKind.ReviewRequired,
                _ => FindingKind.Information
            }
        };
        }).DistinctBy(f => f.Id).ToArray();
    }
    private static Frame? LookupFrame(InspectionContext context, string? id) => context.Input.Model.Frames?.Items.FirstOrDefault(f => f.Id == id);
    public static Rule Create(string id, string title, string method, string[] inputs,
        Func<InspectionContext, IReadOnlyList<Observation>> evaluate) => new(new(id, id.Split('-')[0], title, method, inputs), evaluate);
    public static Observation Pass(string message) => new(CheckStatus.PASS, message);
    public static Observation NotVerified(string message) => new(CheckStatus.NOT_VERIFIED, message);
    public static Observation NotApplicable(string message) => new(CheckStatus.NOT_APPLICABLE, message);
}

