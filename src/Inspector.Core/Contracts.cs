
// Summary values use ETABS display units. Null denotes unavailable or inapplicable data.
// Required reinforcement quantities are not provided capacity or utilization ratios.
public sealed record ConcreteColumnDesignResult(string FrameId, string DesignSection, string DesignCombination,
    double? PmmRatio, double? ShearRatioMajor, double? ShearRatioMinor, double? LongitudinalRebarArea)
{
    public int DesignOption { get; init; }
    public double Location { get; init; }
    public double RequiredShearRebarPerLengthMajor { get; init; }
    public double RequiredShearRebarPerLengthMinor { get; init; }
    public string ShearCombinationMajor { get; init; } = "";
    public string ShearCombinationMinor { get; init; } = "";
    public string ErrorSummary { get; init; } = "";
    public string WarningSummary { get; init; } = "";
}

// Top/bottom and longitudinal torsion values are required areas [L²].
// Shear and transverse torsion values are required reinforcement per unit length [L²/L].
public sealed record ConcreteBeamDesignResult(string FrameId, string DesignSection, double Location,
    double TopRebarArea, double BottomRebarArea, double ShearRebarArea, double TorsionRebarArea,
    string FlexuralCombo, string ShearCombo, string ErrorSummary, string WarningSummary)
{
    // FlexuralCombo is the top reinforcement combination; bottom may be governed independently.
    public string BottomFlexuralCombo { get; init; } = "";
    public string TorsionCombo { get; init; } = "";
    public double TransverseTorsionRebarPerLength { get; init; }
    public string TransverseTorsionCombo { get; init; } = "";
}

// No CSi types, execution, saving, or model mutation in this contract.
// A session is confined to the thread that creates it.
// RequiredVerticalRebarArea is a total area [L²] where derivable; RequiredShearRebarArea
// retains ShearAv [L²/L]. Null means unavailable/inapplicable, not zero or a passing result.
public sealed record ConcretePierDesignResult(string PierName, string StoryName, string? FlexuralCombo,
    string? ShearCombo, double? PmmRatio, double? RequiredVerticalRebarArea, double? RequiredShearRebarArea,
    string ErrorSummary, string WarningSummary)
{
    public string Station { get; init; } = "";
    public string PierLeg { get; init; } = "";
    public string DesignType { get; init; } = "";
    public double RequiredVerticalRebarPercent { get; init; }
    public double CurrentVerticalRebarPercent { get; init; }
    public double RawDemandCapacityRatio { get; init; }
    public double LeftBoundaryRebarArea { get; init; }
    public double RightBoundaryRebarArea { get; init; }
    public double? LegLength { get; init; }
    public double? LegThickness { get; init; }
}

public interface IConcreteDesignSession
{
    DataSet<ConcreteColumnDesignResult> ReadConcreteColumnDesign();
    DataSet<ConcreteBeamDesignResult> ReadConcreteBeamDesign();
    DataSet<ConcretePierDesignResult> ReadConcretePierDesign();
}

public interface INavigationSession
{
    void SelectElements(IEnumerable<string> elementIds);
}

// TotalWeight is force in ETABS display force units; TotalVolume uses display length cubed.
// This is a modeled-material takeoff, not a provided-reinforcement schedule or steel mass in kg.
public sealed record MaterialTakeoffResult(string ObjectType, string Material, double TotalWeight, double TotalVolume);

public interface IModelSession : IDisposable
{
    ModelIdentity ReadIdentity();
    StoryData ReadStories();
    DataSet<Joint> ReadJoints();
    DataSet<Frame> ReadFrames();
    DataSet<Area> ReadAreas();
    DataSet<Material> ReadMaterials();
    DataSet<FrameSection> ReadFrameSections();
    DataSet<AreaSection> ReadAreaSections();
    DataSet<MaterialTakeoffResult> ReadMaterialTakeoff();
}

public sealed class ApiException(string operation, int? returnCode, string message, Exception? inner = null)
    : Exception($"{operation}: {message}" + (returnCode is null ? "" : $" (return code {returnCode})"), inner)
{
    public string Operation { get; } = operation;
    public int? ReturnCode { get; } = returnCode;
}
public sealed class ConnectionException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ConfigurationException(string message) : Exception(message);
public sealed class ModelChangedException(string message) : Exception(message);

public enum ExitCode
{
    Success = 0, EngineeringFailures = 1, Incomplete = 2, ConfigurationError = 3,
    ConnectionFailure = 4, ApiFailure = 5, Cancelled = 130
}

public static class VersionPolicy
{
    public const string RequiredVersion = "22.7.0";
    public static void Validate(string actual)
    {
        if (!Version.TryParse(actual.Trim(), out var version) || version.Major != 22 ||
            version.Minor != 7 || version.Build != 0)
            throw new ConnectionException($"ETABS {actual} is unsupported. This build requires ETABS {RequiredVersion}.");
    }
}

public static class ModelConsistency
{
    public static bool SameContext(ModelIdentity before, ModelIdentity after) =>
        StringComparer.OrdinalIgnoreCase.Equals(before.FilePath, after.FilePath) &&
        before.EtabsVersion == after.EtabsVersion && before.IsLocked == after.IsLocked &&
        before.DisplayUnits == after.DisplayUnits && before.DatabaseUnits == after.DatabaseUnits;

    // Exclude read timestamps and execution duration; compare the values that would be used downstream.
    public static string ContentFingerprint(ModelData data) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            data.Identity.FilePath, data.Identity.EtabsVersion, data.Identity.IsLocked,
            data.Identity.DisplayUnits, data.Identity.DatabaseUnits,
            BaseElevation = data.Stories?.BaseElevation, Stories = data.Stories?.Stories,
            Joints = data.Joints?.Items, Frames = data.Frames?.Items, Areas = data.Areas?.Items,
            Materials = data.Materials?.Items, FrameSections = data.FrameSections?.Items, AreaSections = data.AreaSections?.Items
        })));
}


