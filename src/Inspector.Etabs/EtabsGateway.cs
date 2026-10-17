



// Source: ApiGuard.cs
public static class MaterialTakeoffParser
{
    public static IReadOnlyList<MaterialTakeoffResult> Parse(DatabaseTable table, IReadOnlyList<Material>? materials = null)
    {
        const string requestedKey = "Material List 1 - By Object Type";
        bool derive = !table.Fields.Contains("TotalVolume");
        if (table.Key == requestedKey)
            table.RequireFields("ObjectType", "Material", "TotalWeight", "TotalVolume");
        else if (derive)
            table.RequireFields("ObjectType", "Material", "Weight");
        else table.RequireFields("ObjectType", "Material", "TotalWeight", "TotalVolume");
        var operation = "MaterialTakeoff:" + table.Key;
        var weights = materials?.GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        List<MaterialTakeoffResult> output = [];
        foreach (var row in table.Rows)
        {
            var objectType = row["ObjectType"]; var materialName = row["Material"];
            if (string.IsNullOrWhiteSpace(objectType) || string.IsNullOrWhiteSpace(materialName))
                throw new ApiException(operation, null, "ObjectType and Material must be populated.");
            var weight = Number(row, derive ? "Weight" : "TotalWeight");
            double volume;
            if (derive)
            {
                if (weights is null || !weights.TryGetValue(materialName, out var matches) || matches.Length != 1 ||
                    !double.IsFinite(matches[0].UnitWeight) || matches[0].UnitWeight <= 0)
                    throw new ApiException(operation, null, $"Unique positive material unit weight is required to derive volume for '{materialName}'.");
                volume = ApiGuard.Finite(operation, weight / matches[0].UnitWeight);
            }
            else volume = Number(row, "TotalVolume");
            output.Add(new(objectType, materialName, weight, volume));
        }
        return output.OrderBy(r => r.ObjectType, StringComparer.Ordinal).ThenBy(r => r.Material, StringComparer.Ordinal).ToArray();

        double Number(IReadOnlyDictionary<string, string> row, string field)
        {
            if (!row.TryGetValue(field, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ApiException(operation, null, $"Missing or malformed material quantity '{field}'.");
            ApiGuard.Finite(operation, value);
            if (value < 0) throw new ApiException(operation, null, $"Negative material quantity '{field}'.");
            return value;
        }
    }
}

public static class ConnectionGuard
{
    public static T RequireActiveInstance<T>(T? instance) where T : class => instance ??
        throw new ConnectionException("No active ETABS instance was found. Open ETABS 22.7.0 and a model first.");
}

public sealed class ApiGuard(ILogger logger, CancellationToken cancellation)
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    public void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("An ETABS session cannot be used across threads.");
        cancellation.ThrowIfCancellationRequested();
    }
    public T Read<T>(string operation, Func<T> read)
    {
        CheckThread();
        try
        {
            var value = read();
            logger.LogDebug("API {Operation} completed", operation);
            return value;
        }
        catch (COMException ex)
        {
            logger.LogError(ex, "COM failure in {Operation}; HRESULT {HResult}", operation, ex.HResult);
            throw new ApiException(operation, null, ex.Message, ex);
        }
    }
    public void Call(string operation, Func<int> call)
    {
        var code = Read(operation, call);
        logger.LogDebug("API {Operation} returned {ReturnCode}", operation, code);
        if (code != 0) throw new ApiException(operation, code, "ETABS did not return valid data");
    }
    public static void Arrays(string operation, int expected, params Array?[] arrays)
    {
        if (expected < 0 || arrays.Any(a => (a?.Length ?? 0) != expected))
            throw new ApiException(operation, null, "Returned arrays do not match the reported count");
    }
    public static double Finite(string operation, double value)
    {
        if (!double.IsFinite(value)) throw new ApiException(operation, null, "Returned a non-finite numeric value");
        return value;
    }
}

// Source: CapabilityRegistry.cs
public static class CapabilityRegistry
{
    // Evidence is version/model scoped; SUPPORTED is not an engineering verification classification.
    private static ApiCapability Supported(string id, string feature, string api, string inputs, string[] fields, string limitations = "") =>
        new(id, [feature], api, "22.7.0", "22.7.0", inputs, fields, limitations,
            CapabilityState.SUPPORTED, "Live read and comparison tests on Commercial_6Story_IraqiCode, 2026-10-15; see docs/PHASE_1_REPORT.md");

    public static IReadOnlyList<ApiCapability> All { get; } = Array.AsReadOnly(new[]
    {
        Supported("API-CONNECT", "Connection", "cHelper.GetObject; cSapModel.GetVersion", "Running local ETABS under the same user and elevation",
            ["SapModel", "Version"], "Attach only; active instance chosen by ETABS; not an instance picker."),
        Supported("API-IDENTITY", "Identity and units", "GetModelFilename; GetModelIsLocked; GetPresentUnits_2; GetDatabaseUnits_2",
            "Named model", ["FilePath", "IsLocked", "Force", "Length", "Temperature"], "No atomic revision token; context checks plus two content reads are used."),
        Supported("API-STORIES", "Stories", "cTower.GetNameList; cStory.GetStories_2", "Single-tower model", ["BaseElevation", "Name", "Elevation", "Height", "IsMaster", "SimilarTo"],
            "Multi-tower models are explicitly rejected for story extraction."),
        Supported("API-POINTS", "Joint geometry", "cPointObj.GetAllPoints", "Global coordinates", ["Name", "X", "Y", "Z"]),
        Supported("API-FRAMES", "Frame geometry", "cFrameObj.GetAllFrames", "Global coordinates", ["Name", "Story", "Section", "Point1", "Point2", "Coordinates", "LocalAxisAngle"],
            "Object geometry only; offsets and analysis mesh are not yet exposed."),
        Supported("API-AREAS", "Area geometry", "cAreaObj.GetNameList/GetPoints/GetLabelFromName/GetProperty/GetOpening/GetDesignOrientation",
            "Defined area objects", ["Name", "Label", "Story", "Section", "JointIds", "IsOpening", "Orientation"], "Per-object boundaries; bulk delimiter behavior remains untested."),
        Supported("API-MATERIALS", "Material definitions", "cPropMaterial.GetNameList/GetTypeOAPI/GetWeightAndMass/GetMPIsotropic/GetMPUniaxial/GetOConcrete/GetORebar",
            "Isotropic or uniaxial materials at default API temperature 0", ["Type", "Symmetry", "Weight", "Mass", "E", "PoissonRatio", "Fc", "Fy", "Fu"],
            "No temperature curves or anisotropic/orthotropic stiffness; no provided reinforcement is inferred."),
        Supported("API-RECTANGLE", "Rectangular sections", "cPropFrame.GetRectangle", "Rectangular frame definition", ["Material", "Depth", "Width"]),
        Supported("API-I-SECTION", "I-section definitions", "cPropFrame.GetISection", "I-shaped frame definition", ["Material", "Depth", "TopWidth", "TopThickness", "WebThickness", "BottomWidth", "BottomThickness"],
            "Extraction only; steel design inspection is outside product scope."),
        Supported("API-AUTO-SELECT", "Auto-select definitions", "cPropFrame.GetAutoSelectSteel", "Auto-select property", ["Candidates", "AutoStartSection"],
            "The list itself has no single physical width, depth, or material."),
        Supported("API-SLAB-WALL", "Slab and wall definitions", "cPropArea.GetSlab/GetWall", "Homogeneous slab or specified wall", ["ShellType", "Material", "MembraneThickness"],
            "Layered, ribbed, waffle, and auto-select wall details are incomplete and produce limitations."),
        Supported("API-FILLED-DECK", "Filled deck geometry", "cPropArea.GetDeck/GetDeckFilled", "Filled deck property", ["Material", "MembraneThickness", "SlabDepth", "RibDepth", "RibWidths", "RibSpacing", "DeckShearThickness"],
            "Geometry only; no composite deck design checks."),
        Supported("API-AREA-TABLE", "Area definition classification", "DatabaseTables.GetTableForDisplayArray:Area Section Property Definitions - Summary",
            "All group; required fields Name, Type, AnalType", ["Name", "Type", "AnalType"], "Localized/changed table values must be revalidated."),
        Phase2("API-LOAD-PATTERNS", "Load patterns", "LoadPatterns.GetNameList/GetLoadType/GetSelfWTMultiplier", ["Name", "Type", "SelfWeightMultiplier"]),
        Phase2("API-LOAD-CASES", "Case definitions", "LoadCases.GetNameList/GetTypeOAPI; StaticLinear.GetLoads", ["Name", "Type", "SubType", "LoadType", "LoadName", "Factor"], "Load components extracted only for linear static cases; other cases have explicit null component data."),
        Phase2("API-COMBINATIONS", "Combination definitions", "RespCombo.GetNameList/GetTypeCombo/GetCaseList_1", ["Name", "Type", "MemberType", "MemberName", "Mode", "Factor"]),
        Phase2("API-MASS-SOURCES", "All mass sources", "DatabaseTables:Mass Source Definition", ["Name", "IsDefault", "SourceSelf", "SourceAdded", "SourceLoads", "LoadPattern", "Multiplier"], "Sparse continuation flags parsed by source; default source compared with PropMaterial.GetMassSource_1. Multiple-source live example remains untested."),
        Phase2("API-DIAPHRAGMS", "Diaphragm definitions/assignments", "Diaphragm.GetNameList/GetDiaphragm; PointObj.GetDiaphragm; AreaObj.GetDiaphragm", ["Name", "SemiRigid", "JointAssignmentOption", "AreaDiaphragm"], "Rigid and inherited/explicit assignments verified. Semi-rigid positive example remains untested."),
        Phase2("API-RESTRAINT-RELEASE", "Restraints and releases", "PointObj.GetRestraint; FrameObj.GetReleases; FrameObj.GetTypeOAPI", ["Restraints", "EndReleases", "PartialFixity", "GeometryType"], "GetTypeOAPI returns Straight/Curved geometry, not beam/column classification. Positive release/partial-fixity examples await live validation."),
        Phase2("API-OVERWRITE-ABSENCE", "Material overwrite absence", "DatabaseTables.GetAllTables:IsEmpty for Frame/Area Assignments - Material Overwrites", ["IsEmpty"], "Empty tables verified. Nonempty tables parse UniqueName/Material but cause unverified material-reference checks until live validation."),
        Phase2("API-LINK-INVENTORY", "Link inventory", "LinkObj.GetNameList", ["Name"], "Acceptance model contains no links. Nonempty endpoint acquisition is explicitly limited."),
        new ApiCapability("API-ANALYSIS-STATUS", ["Analysis case status"], "cAnalyze.GetCaseStatus", "22.7.0", "22.7.0",
            "Named ETABS model", ["CaseName", "Status (1=Not run, 2=Could not start, 3=Not finished, 4=Finished)"],
            "Signature and status meanings confirmed against CSI API help and the installed ETABSv1.dll. A Finished status alone does not establish result freshness or engineering validity.",
            CapabilityState.SUPPORTED, "Phase3LiveTests.AnalysisCaseStatusMatchesRawGetCaseStatus; 2026-10-15; see docs/PHASE_3_REPORT.md"),
        new ApiCapability("API-ANALYSIS-RESULTS", ["Selected analysis results"], "cAnalysisResults.StoryDrifts/JointDrifts/ModalParticipatingMassRatios/BaseReact; Results.Setup selection getters; DatabaseTables modal fallback", "22.7.0", "22.7.0",
            "Analyzed model; existing ETABS output selection is read without changes", ["Story drift", "Matched point displacement", "Modal participating mass ratios", "Base reactions and reference point"],
            "Output selection is never changed. Empty selections and failed result calls are reported as data-read limitations, not missing model inputs. Unselected results and result freshness are not verified. StoryDrifts X/Y/Z are coordinates on the acceptance model; displacement is matched from JointDrifts.",
            CapabilityState.SUPPORTED, "AnalysisResultApiDiagnostics.ResultApiCallShapesAndTablesAreRecordedReadOnly; 2026-10-15; docs/PHASE_3_RESULT_EXTRACTION.md"),
        new ApiCapability("API-SEISMIC-DEFINITIONS", ["Response spectrum and geometric nonlinearity definitions"], "LoadCases.ResponseSpectrum.GetLoads/GetModalCase/GetModalComb_1/GetDirComb; LoadCases.StaticNonlinear.GetGeometricNonlinearity", "22.7.0", "22.7.0",
            "Existing response-spectrum and regular nonlinear static cases", ["Spectrum functions/scales/directions", "Modal case and raw combination options", "Raw geometric nonlinearity option"],
            "Signatures checked against installed ETABSv1.dll and exercised live through inspect seismic. Individual definition values are inventoried but not independently cross-checked. Does not verify spectrum scaling, code applicability, stability, staged nonlinear cases, or global P-Delta configuration.",
            CapabilityState.SUPPORTED, "Phase3LiveTests.SeismicDefinitionsAreExtractedAndInventoried; 2026-10-15; see docs/PHASE_3_REPORT.md"),
        new ApiCapability("API-ANALYSIS-EXTENDED", ["Extended analysis evidence"], "DatabaseTables.GetTableForDisplayArray; AnalysisResults.ModeShape/FrameForce; FrameObj/AreaObj/PropFrame/PropArea.GetModifiers; DesignConcrete.GetCode/GetResultsAvailable", "22.7.0", "22.7.0",
            "Analyzed ETABS 22.7.0 model with all finished analysis cases and all defined response combinations selected for output", ["Analysis messages", "Joint displacements", "Modal periods", "Story stiffness/gravity ratios", "Diaphragm drift ratios", "Auto-seismic inputs", "Mass summary", "Mode shapes", "Frame forces", "Object/section modifiers", "Concrete design code and result-availability flag"],
            "Live read-only extraction produced 11 evidence tables, 75,824 mode-shape rows across selected cases and 130,368 frame-force rows. The acceptance model's modal case returned shape rows for Mode 1 only while its period table lists 12 modes; missing Mode 2-12 shapes remain NOT VERIFIED. Table availability, selected-output coverage and analysis freshness are model-scoped.",
            CapabilityState.SUPPORTED, "phase3-final-review.json / SEIS-005; REA inspect evidence ev_28645d4244464dd9cb72ee522d99719cb9cb5d54bf114dfbe0c68000548f1a6e; 2026-10-15"),
        new ApiCapability("API-ANALYSIS-RUN", ["Analysis execution is disabled"], "None", "22.7.0", null,
            "Read-only inspector", [], "Analysis must be run in ETABS. The inspector rejects analysis execution.",
            CapabilityState.UNSUPPORTED, "Read-only regression tests"),
        new ApiCapability("API-FRAME-OVERWRITE-GETTER", ["Material overwrite getter"], "FrameObj.GetMaterialOverwrite", "22.7.0", "22.7.0",
            "Frame without overwrite", [], "Returned code 1 on a valid frame without an overwrite; not used. Table inventory distinguishes absence from errors.", CapabilityState.FAILED,
            "Observed live in Phase 2 acquisition testing, 2026-10-15"),
        new ApiCapability("API-CIRCLE", ["Circular sections"], "cPropFrame.GetCircle", "22.7.0", null,
            "Circular frame definition", ["Material", "Diameter"], "Signature confirmed; no circular section exists in the live acceptance model. Extraction is explicitly partial if encountered.",
            CapabilityState.UNTESTED, "Awaiting representative live model comparison"),
        new ApiCapability("API-AREAS-BULK", ["Bulk area boundaries"], "cAreaObj.GetAllAreas", "22.7.0", null,
            "Area objects", ["Names", "PointDelimiter", "Points"], "Local documentation does not explain delimiter semantics. Use GetPoints instead.",
            CapabilityState.UNTESTED, "Not used by the extractor")
    });

    private static ApiCapability Phase2(string id, string feature, string api, string[] fields, string limitations = "") =>
        new(id, [feature], api, "22.7.0", "22.7.0", "Live entire model in ETABS 22.7.0", fields, limitations,
            CapabilityState.SUPPORTED, "Phase2LiveTests; 2026-10-15; see docs/PHASE_2_REPORT.md");
}

// Source: DatabaseTable.cs
public sealed record DatabaseTable(string Key, int Version, IReadOnlyList<string> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)
{
    public static DatabaseTable Parse(string key, int version, string[] fields, int records, string[] data)
    {
        if (records < 0 || (long)fields.Length * records != data.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(string.IsNullOrWhiteSpace) || (records > 0 && fields.Length == 0))
            throw new ApiException($"DatabaseTables:{key}", null, "Malformed table fields or row count");
        return new(key, version, fields, Enumerable.Range(0, records).Select(row =>
            (IReadOnlyDictionary<string, string>)fields.Select((field, col) => (field, value: data[row * fields.Length + col] ?? ""))
                .ToDictionary(pair => pair.field, pair => pair.value, StringComparer.Ordinal)).ToArray());
    }
    public void RequireFields(params string[] required)
    {
        var missing = required.Except(Fields, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new ApiException($"DatabaseTables:{Key}", null, $"Required fields missing: {string.Join(", ", missing)}");
    }
}

public sealed partial class EtabsSession
{
    public DatabaseTable ReadTable(string key)
    {
        Check();
        string[] requested = [""], included = [], data = []; int version = 0, count = 0;
        guard.Call($"DatabaseTables.GetTableForDisplayArray:{key}", () => tableApi.GetTableForDisplayArray(
            key, ref requested, "All", ref version, ref included, ref count, ref data));
        return DatabaseTable.Parse(key, version, included ?? [], count, data ?? []);
    }
}

// Source: EtabsSession.cs
public sealed partial class EtabsSession : IModelSession, IAnalysisSession, IConcreteDesignSession, IConcreteDesignStateSession, INavigationSession
{
    private readonly ApiGuard guard;
    private readonly ILogger logger;
    private readonly List<object> references = [];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly cSapModel model;
    private readonly cStory storyApi;
    private readonly cTower towerApi;
    private readonly cPointObj pointApi;
    private readonly cFrameObj frameApi;
    private readonly cAreaObj areaApi;
    private readonly cPropMaterial materialApi;
    private readonly cPropFrame framePropertyApi;
    private readonly cPropArea areaPropertyApi;
    private readonly cDatabaseTables tableApi;
    private readonly cAnalyze analyzeApi;
    private UnitContext units = new("Unknown", "Unknown", "Unknown");
    private bool disposed;

    public static EtabsSession Attach(ILogger logger, CancellationToken cancellation = default)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Use StaRunner to create the ETABS session.");
        logger.LogInformation("Attaching to the active ETABS instance");
        try { return new EtabsSession(logger, cancellation); }
        catch (COMException ex) { throw new ConnectionException("Cannot attach to ETABS. Open ETABS 22.7.0 with a model and use the same Windows user/elevation level.", ex); }
    }

    private EtabsSession(ILogger logger, CancellationToken cancellation)
    {
        this.logger = logger;
        guard = new(logger, cancellation);
        try
        {
            cHelper helper = Own<cHelper>(new Helper());
            var app = Own(ConnectionGuard.RequireActiveInstance(helper.GetObject("CSI.ETABS.API.ETABSObject")));
            model = Own(app.SapModel ?? throw new ConnectionException("ETABS has no available model."));
            VersionPolicy.Validate(ReadIdentity().EtabsVersion);
            storyApi = Own(model.Story);
            towerApi = Own(model.Tower);
            pointApi = Own(model.PointObj);
            frameApi = Own(model.FrameObj);
            areaApi = Own(model.AreaObj);
            materialApi = Own(model.PropMaterial);
            framePropertyApi = Own(model.PropFrame);
            areaPropertyApi = Own(model.PropArea);
            tableApi = Own(model.DatabaseTables);
            analyzeApi = Own(model.Analyze);
        }
        catch { Dispose(); throw; }
    }

    public void RunAnalysis()
    {
        Check();
        throw new InvalidOperationException("This inspector is read-only. Run analysis in ETABS when needed.");
    }

    public AnalysisStatusData ReadAnalysisStatus()
    {
        Check();
        int count = 0; string[] names = []; int[] states = [];
        guard.Call("Analyze.GetCaseStatus", () => analyzeApi.GetCaseStatus(ref count, ref names, ref states));
        ApiGuard.Arrays("Analyze.GetCaseStatus", count, names, states);
        var cases = Enumerable.Range(0, count).Select(i =>
        {
            var state = Enum.IsDefined(typeof(AnalysisCaseState), states[i])
                ? (AnalysisCaseState)states[i] : AnalysisCaseState.Unknown;
            return new AnalysisCaseStatus(names[i], state, states[i]);
        }).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        return new(new(cases, [new("Analyze.GetCaseStatus", DateTimeOffset.UtcNow, units)],
            cases.Any(c => c.State == AnalysisCaseState.Unknown)
                ? ["ETABS returned an undocumented analysis-case status; it is not interpreted as a pass."] : []));
    }

    public DataSet<MaterialTakeoffResult> ReadMaterialTakeoff()
    {
        Check();
        const string requestedKey = "Material List 1 - By Object Type";
        const string installedKey = "Material List by Object Type";
        const string inventoryOperation = "DatabaseTables.GetAllTables:MaterialTakeoff";
        int count = 0; string[] keys = [], names = []; int[] importTypes = []; bool[] empty = [];
        guard.Call(inventoryOperation, () => tableApi.GetAllTables(ref count, ref keys, ref names, ref importTypes, ref empty));
        ApiGuard.Arrays(inventoryOperation, count, keys, names, importTypes, empty);
        var key = keys.Contains(requestedKey, StringComparer.Ordinal) ? requestedKey :
            keys.Contains(installedKey, StringComparer.Ordinal) ? installedKey :
            throw new ApiException(inventoryOperation, null, "A supported material takeoff table is unavailable.");
        var table = ReadTable(key);
        var deriveVolume = !table.Fields.Contains("TotalVolume");
        var materials = deriveVolume ? ReadMaterials() : null;
        var items = MaterialTakeoffParser.Parse(table, materials?.Items);
        var operations = new[] { inventoryOperation, $"DatabaseTables.GetTableForDisplayArray:{key}" }
            .Concat(materials?.Sources.Select(s => s.Operation) ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var limitations = new List<string>
        {
            $"Modeled material quantities use display units: weight={units.Force}, volume={units.Length}³. Weight is force, not mass in kg. This table does not establish provided reinforcement quantity."
        };
        if (deriveVolume) limitations.Add("ETABS 22.7 exposes Weight but no TotalVolume in Material List by Object Type. TotalVolume is derived as Weight / matching PropMaterial.GetWeightAndMass unit weight; no geometry-based or reinforcement allowance is invented.");
        if (items.Count == 0) limitations.Add("The material takeoff table is empty; total building quantities are not verified.");
        return Data(items, operations, limitations.Concat(materials?.Limitations ?? []));
    }

    public void SelectElements(IEnumerable<string> elementIds)
    {
        Check();
        ArgumentNullException.ThrowIfNull(elementIds);
        var targets = elementIds.Select(value =>
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An element ID cannot be empty.", nameof(elementIds));
            var area = value.StartsWith("Area:", StringComparison.OrdinalIgnoreCase);
            var id = area ? value[5..] : value.StartsWith("Frame:", StringComparison.OrdinalIgnoreCase) ? value[6..] : value;
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An element prefix must be followed by an ID.", nameof(elementIds));
            return (Area: area, Id: id);
        }).Distinct().ToArray();
        var selection = Own(model.SelectObj);
        guard.Call("SelectObj.ClearSelection", () => selection.ClearSelection());
        foreach (var target in targets)
        {
            if (target.Area)
                guard.Call($"AreaObj.SetSelected:{target.Id}", () => areaApi.SetSelected(target.Id, true, eItemType.Objects));
            else
                guard.Call($"FrameObj.SetSelected:{target.Id}", () => frameApi.SetSelected(target.Id, true, eItemType.Objects));
        }
        var view = Own(model.View);
        guard.Call("View.RefreshView", () => view.RefreshView(0, false));
    }

    public ConcreteDesignStatusData ReadConcreteDesignStatus()
    {
        Check();
        bool? available = null;
        List<ExtractionIssue> issues = [];
        List<ConcreteDesignMember> members = [];
        try { available = guard.Read("DesignConcrete.GetResultsAvailable", () => model.DesignConcrete.GetResultsAvailable()); }
        catch (ApiException ex) { issues.Add(new("ConcreteDesignStatus", ex.Message, ex.Operation, ex.ReturnCode)); }
        int count = 0; string[] names = [];
        try
        {
            guard.Call("FrameObj.GetNameList:DesignCoverage", () => frameApi.GetNameList(ref count, ref names));
            ApiGuard.Arrays("FrameObj.GetNameList:DesignCoverage", count, names);
            foreach (var name in names)
            {
                try
                {
                    int procedure = -1; eFrameDesignOrientation orientation = default;
                    guard.Call($"FrameObj.GetDesignProcedure:{name}", () => frameApi.GetDesignProcedure(name, ref procedure));
                    guard.Call($"FrameObj.GetDesignOrientation:{name}", () => frameApi.GetDesignOrientation(name, ref orientation));
                    members.Add(new(name, orientation.ToString(), procedure));
                }
                catch (ApiException ex) { issues.Add(new("ConcreteDesignMembers", ex.Message, ex.Operation, ex.ReturnCode)); }
            }
        }
        catch (ApiException ex) { issues.Add(new("ConcreteDesignMembers", ex.Message, ex.Operation, ex.ReturnCode)); }
        return new(available, Data(members, ["FrameObj.GetDesignProcedure", "FrameObj.GetDesignOrientation"],
            issues.Where(i => i.Component == "ConcreteDesignMembers").Select(i => i.Message)), issues);
    }

    public DataSet<ConcreteColumnDesignResult> ReadConcreteColumnDesign()
    {
        Check();
        const string operation = "DesignConcrete.GetSummaryResultsColumn";
        var design = Own(model.DesignConcrete);
        var available = false;
        var operations = new[] { "DesignConcrete.GetResultsAvailable", operation, "DesignConcrete.GetDesignSection" };
        try
        {
            available = guard.Read("DesignConcrete.GetResultsAvailable", () => design.GetResultsAvailable());
            if (!available)
                return Data(Array.Empty<ConcreteColumnDesignResult>(), ["DesignConcrete.GetResultsAvailable"]);

            int count = 0;
            string[] frames = [], pmmCombos = [], majorCombos = [], minorCombos = [], errors = [], warnings = [];
            int[] options = [];
            double[] locations = [], pmmAreas = [], pmmRatios = [], majorAreas = [], minorAreas = [];
            guard.Call(operation, () => design.GetSummaryResultsColumn("All", ref count, ref frames,
                ref options, ref locations, ref pmmCombos, ref pmmAreas, ref pmmRatios,
                ref majorCombos, ref majorAreas, ref minorCombos, ref minorAreas,
                ref errors, ref warnings, eItemType.Group));
            ApiGuard.Arrays(operation, count, frames, options, locations, pmmCombos, pmmAreas,
                pmmRatios, majorCombos, majorAreas, minorCombos, minorAreas, errors, warnings);

            List<ConcreteColumnDesignResult> items = [];
            List<string> limitations = [
                "Shear utilization ratios are unavailable from GetSummaryResultsColumn; AVMajor/AVMinor are required transverse reinforcement per unit length, not demand/capacity ratios.",
                "PMM ratio applies only to Check mode (MyOption=1); longitudinal rebar area is required area only in Design mode (MyOption=2), not provided reinforcement. Design freshness is not established."
            ];
            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (!sections.TryGetValue(frames[i], out var section))
                {
                    section = "";
                    guard.Call($"DesignConcrete.GetDesignSection:{frames[i]}", () => design.GetDesignSection(frames[i], ref section));
                    sections.Add(frames[i], section);
                }
                errors[i] = NormalizeDesignMessage(errors[i]); warnings[i] = NormalizeDesignMessage(warnings[i]);
                if (!string.IsNullOrWhiteSpace(errors[i])) limitations.Add($"{frames[i]}: design error: {errors[i]}");
                if (!string.IsNullOrWhiteSpace(warnings[i])) limitations.Add($"{frames[i]}: design warning: {warnings[i]}");
                if (options[i] is not (1 or 2)) limitations.Add($"{frames[i]}: unsupported concrete design option {options[i]}.");
                var valid = string.IsNullOrWhiteSpace(errors[i]);
                items.Add(new(frames[i], section, pmmCombos[i],
                    valid && options[i] == 1 ? ApiGuard.Finite(operation, pmmRatios[i]) : null,
                    null, null, valid && options[i] == 2 ? ApiGuard.Finite(operation, pmmAreas[i]) : null)
                {
                    DesignOption = options[i], Location = ApiGuard.Finite(operation, locations[i]),
                    RequiredShearRebarPerLengthMajor = ApiGuard.Finite(operation, majorAreas[i]),
                    RequiredShearRebarPerLengthMinor = ApiGuard.Finite(operation, minorAreas[i]),
                    ShearCombinationMajor = majorCombos[i], ShearCombinationMinor = minorCombos[i],
                    ErrorSummary = errors[i], WarningSummary = warnings[i]
                });
            }
            if (count == 0) limitations.Add("ETABS returned no concrete column design summary rows; column coverage is not verified.");
            return Data(items, operations, limitations.Distinct(StringComparer.Ordinal));
        }
        catch (ApiException ex)
        {
            logger.LogWarning(ex, "Concrete column design extraction is unavailable");
            return Data(Array.Empty<ConcreteColumnDesignResult>(), [ex.Operation], [
                "Concrete column design data could not be read. This does not show whether design has been run.",
                ex.Message
            ]) with { ReadSucceeded = false };
        }
    }

    public DataSet<ConcretePierDesignResult> ReadConcretePierDesign()
    {
        Check();
        const string operation = "DesignShearWall.GetPierSummaryResults";
        List<string> operations = ["DatabaseTables.GetAllTables:PierDesignAvailability"];
        List<string> limitations = [
            "ETABS 22.7 cDesignShearWall has no GetResultsAvailable method; availability is checked using nonempty Shear Wall Pier Design Summary tables.",
            "The pier summary API exposes no governing flexural/shear combinations. PmmRatio is unavailable unless a positive DCRatio is explicitly reported in Check mode; RawDemandCapacityRatio is preserved separately.",
            "RequiredVerticalRebarArea is derived only for Uniform piers as ReinfPercent/100 * leg length * thickness using matching summary-table geometry. Boundary areas are retained separately and are not assumed to be total vertical reinforcement.",
            "RequiredShearRebarArea is the API ShearAv: required area per unit length [L²/L], not a total area or utilization ratio. Required areas use ETABS display units. Design freshness and provided reinforcement adequacy are not established."
        ];
        try
        {
            int tableCount = 0; string[] keys = [], names = []; int[] importTypes = []; bool[] empty = [];
            guard.Call(operations[0], () => tableApi.GetAllTables(ref tableCount, ref keys, ref names, ref importTypes, ref empty));
            ApiGuard.Arrays(operations[0], tableCount, keys, names, importTypes, empty);
            var summaryIndexes = Enumerable.Range(0, tableCount)
                .Where(i => keys[i].StartsWith("Shear Wall Pier Design Summary - ", StringComparison.Ordinal)).ToArray();
            var available = summaryIndexes.Where(i => !empty[i]).ToArray();
            if (summaryIndexes.Length > 0 && available.Length == 0)
                return Data(Array.Empty<ConcretePierDesignResult>(), operations.ToArray(), limitations.Append("Shear Wall Design needs to be run first in ETABS; all pier design summary tables are empty."));

            DatabaseTable? geometry = null;
            if (available.Length == 1)
            {
                var key = keys[available[0]];
                try
                {
                    operations.Add($"DatabaseTables.GetTableForDisplayArray:{key}");
                    geometry = ReadTable(key);
                    geometry.RequireFields("Story", "Pier", "Station", "PierLeg", "Length", "Thickness");
                }
                catch (ApiException ex)
                {
                    geometry = null;
                    limitations.Add("Pier geometry supplementation is unavailable; vertical total area cannot be derived. " + ex.Message);
                }
            }
            else limitations.Add("A unique nonempty pier summary table is unavailable; vertical area derivation is disabled.");
            var geometryByLeg = geometry?.Rows.GroupBy(r => (r["Story"], r["Pier"], r["Station"], r["PierLeg"]))
                .ToDictionary(g => g.Key, g => g.ToArray());

            string[] stories = [], piers = [], stations = [], types = [], sectionTypes = [], edgeBars = [], endBars = [], legs = [], warnings = [], errors = [];
            double[] spacing = [], requiredPercent = [], currentPercent = [], dc = [], x1 = [], y1 = [], x2 = [], y2 = [],
                edgeLeft = [], edgeRight = [], asLeft = [], asRight = [], shear = [], stressLeft = [], stressRight = [],
                stressLimitLeft = [], stressLimitRight = [], cLeft = [], cLimitLeft = [], cRight = [], cLimitRight = [],
                rotationDemand = [], rotationCapacity = [], normalStress = [], normalStressLimit = [], cDepth = [],
                boundaryLeft = [], boundaryRight = [], boundaryLength = [];
            var design = Own(model.DesignShearWall);
            operations.Add(operation);
            guard.Call(operation, () => design.GetPierSummaryResults(ref stories, ref piers, ref stations, ref types,
                ref sectionTypes, ref edgeBars, ref endBars, ref spacing, ref requiredPercent, ref currentPercent,
                ref dc, ref legs, ref x1, ref y1, ref x2, ref y2, ref edgeLeft, ref edgeRight, ref asLeft, ref asRight,
                ref shear, ref stressLeft, ref stressRight, ref stressLimitLeft, ref stressLimitRight,
                ref cLeft, ref cLimitLeft, ref cRight, ref cLimitRight, ref rotationDemand, ref rotationCapacity,
                ref normalStress, ref normalStressLimit, ref cDepth, ref boundaryLeft, ref boundaryRight,
                ref boundaryLength, ref warnings, ref errors));
            int count = stories?.Length ?? 0;
            ApiGuard.Arrays(operation, count, stories, piers, stations, types, sectionTypes, edgeBars, endBars,
                spacing, requiredPercent, currentPercent, dc, legs, x1, y1, x2, y2, edgeLeft, edgeRight,
                asLeft, asRight, shear, stressLeft, stressRight, stressLimitLeft, stressLimitRight,
                cLeft, cLimitLeft, cRight, cLimitRight, rotationDemand, rotationCapacity, normalStress,
                normalStressLimit, cDepth, boundaryLeft, boundaryRight, boundaryLength, warnings, errors);
            List<ConcretePierDesignResult> items = [];
            for (int i = 0; i < count; i++)
            {
                var error = Message(errors[i]); var warning = Message(warnings[i]);
                if (error.Length > 0) limitations.Add($"{stories![i]}/{piers[i]}/{stations[i]}/{legs[i]}: design error: {error}");
                if (warning.Length > 0) limitations.Add($"{stories![i]}/{piers[i]}/{stations[i]}/{legs[i]}: design warning: {warning}");
                double? length = null, thickness = null, area = null;
                if (geometryByLeg is not null && geometryByLeg.TryGetValue((stories![i], piers[i], stations[i], legs[i]), out var matches) && matches.Length == 1)
                {
                    length = Positive(matches[0]["Length"]); thickness = Positive(matches[0]["Thickness"]);
                }
                var percent = ApiGuard.Finite(operation, requiredPercent[i]);
                var rawDc = ApiGuard.Finite(operation, dc[i]);
                var rawShear = ApiGuard.Finite(operation, shear[i]);
                if (error.Length == 0 && types[i].Equals("Uniform", StringComparison.OrdinalIgnoreCase) && length is > 0 && thickness is > 0 && percent >= 0)
                    area = ApiGuard.Finite(operation, percent / 100 * length.Value * thickness.Value);
                items.Add(new(piers[i], stories![i], null, null,
                    error.Length == 0 && types[i].Equals("Check", StringComparison.OrdinalIgnoreCase) && rawDc > 0 ? rawDc : null,
                    area, error.Length == 0 && rawShear >= 0 ? rawShear : null, error, warning)
                {
                    Station = stations[i], PierLeg = legs[i], DesignType = types[i],
                    RequiredVerticalRebarPercent = percent, CurrentVerticalRebarPercent = ApiGuard.Finite(operation, currentPercent[i]),
                    RawDemandCapacityRatio = rawDc, LeftBoundaryRebarArea = ApiGuard.Finite(operation, asLeft[i]),
                    RightBoundaryRebarArea = ApiGuard.Finite(operation, asRight[i]), LegLength = length, LegThickness = thickness
                });
            }
            if (items.Any(x => x.RequiredVerticalRebarArea is null)) limitations.Add("Some pier rows lack a valid total required vertical area; consult the retained reinforcement percentage, boundary areas, design type and error fields.");
            if (count == 0) limitations.Add("No pier design rows were returned. Run Shear Wall Design if missing; pier coverage is not verified.");
            return Data(items, operations.ToArray(), limitations.Distinct(StringComparer.Ordinal));
        }
        catch (ApiException ex)
        {
            logger.LogWarning(ex, "Concrete pier design extraction is unavailable");
            return Data(Array.Empty<ConcretePierDesignResult>(), [ex.Operation], limitations.Append("Wall design data could not be read. This does not show whether wall design has been run.").Append(ex.Message)) with { ReadSucceeded = false };
        }
        static string Message(string? text) => string.IsNullOrWhiteSpace(text) || text.Trim().Equals("No Message", StringComparison.OrdinalIgnoreCase) ? "" : text;
        static double? Positive(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 ? value : null;
    }

    public DataSet<ConcreteBeamDesignResult> ReadConcreteBeamDesign()
    {
        Check();
        const string operation = "DesignConcrete.GetSummaryResultsBeam";
        var design = Own(model.DesignConcrete);
        var available = false;
        var operations = new[] { "DesignConcrete.GetResultsAvailable", operation, "DesignConcrete.GetDesignSection" };
        try
        {
            available = guard.Read("DesignConcrete.GetResultsAvailable", () => design.GetResultsAvailable());
            if (!available)
                return Data(Array.Empty<ConcreteBeamDesignResult>(), ["DesignConcrete.GetResultsAvailable"]);
            int count = 0;
            string[] frames = [], topCombos = [], bottomCombos = [], shearCombos = [],
                torsionLongCombos = [], torsionTransverseCombos = [], errors = [], warnings = [];
            double[] locations = [], topAreas = [], bottomAreas = [], shearAreas = [],
                torsionLongAreas = [], torsionTransverseAreas = [];
            guard.Call(operation, () => design.GetSummaryResultsBeam("All", ref count, ref frames,
                ref locations, ref topCombos, ref topAreas, ref bottomCombos, ref bottomAreas,
                ref shearCombos, ref shearAreas, ref torsionLongCombos, ref torsionLongAreas,
                ref torsionTransverseCombos, ref torsionTransverseAreas, ref errors, ref warnings, eItemType.Group));
            ApiGuard.Arrays(operation, count, frames, locations, topCombos, topAreas, bottomCombos,
                bottomAreas, shearCombos, shearAreas, torsionLongCombos, torsionLongAreas,
                torsionTransverseCombos, torsionTransverseAreas, errors, warnings);
            List<ConcreteBeamDesignResult> items = [];
            List<string> limitations = [
                "Beam summary reinforcement is required, not provided. Design freshness is not established. Rows with ErrorSummary must not be treated as valid design verification.",
                "TopRebarArea, BottomRebarArea and TorsionRebarArea (longitudinal torsion) use display length squared. ShearRebarArea and TransverseTorsionRebarPerLength use area per unit length. FlexuralCombo is the top combination; BottomFlexuralCombo retains the bottom combination."
            ];
            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (!sections.TryGetValue(frames[i], out var section))
                {
                    section = "";
                    guard.Call($"DesignConcrete.GetDesignSection:{frames[i]}", () => design.GetDesignSection(frames[i], ref section));
                    sections.Add(frames[i], section);
                }
                if (!string.IsNullOrWhiteSpace(errors[i])) limitations.Add($"{frames[i]}: design error: {errors[i]}");
                if (!string.IsNullOrWhiteSpace(warnings[i])) limitations.Add($"{frames[i]}: design warning: {warnings[i]}");
                items.Add(new(frames[i], section, ApiGuard.Finite(operation, locations[i]),
                    ApiGuard.Finite(operation, topAreas[i]), ApiGuard.Finite(operation, bottomAreas[i]),
                    ApiGuard.Finite(operation, shearAreas[i]), ApiGuard.Finite(operation, torsionLongAreas[i]),
                    topCombos[i], shearCombos[i], NormalizeDesignMessage(errors[i]), NormalizeDesignMessage(warnings[i]))
                {
                    BottomFlexuralCombo = bottomCombos[i], TorsionCombo = torsionLongCombos[i],
                    TransverseTorsionRebarPerLength = ApiGuard.Finite(operation, torsionTransverseAreas[i]),
                    TransverseTorsionCombo = torsionTransverseCombos[i]
                });
            }
            if (count == 0) limitations.Add("ETABS returned no concrete beam design summary rows; beam coverage is not verified.");
            return Data(items, operations, limitations.Distinct(StringComparer.Ordinal));
        }
        catch (ApiException ex)
        {
            logger.LogWarning(ex, "Concrete beam design extraction is unavailable");
            return Data(Array.Empty<ConcreteBeamDesignResult>(), [ex.Operation], [
                "Concrete beam design data could not be read. This does not show whether design has been run.",
                ex.Message
            ]) with { ReadSucceeded = false };
        }
    }

    public AnalysisResultsData ReadSelectedAnalysisResults()
    {
        Check();
        var setup = Own(model.Results.Setup);
        var results = Own(model.Results);
        var statuses = ReadAnalysisStatus().Cases.Items;
        List<string> selectedCases = [];
        foreach (var item in statuses)
        {
            bool selected = false;
            guard.Call($"Results.Setup.GetCaseSelectedForOutput:{item.Name}",
                () => setup.GetCaseSelectedForOutput(item.Name, ref selected));
            if (selected) selectedCases.Add(item.Name);
        }
        var combos = Own(model.RespCombo);
        int comboCount = 0; string[] comboNames = [];
        guard.Call("RespCombo.GetNameList", () => combos.GetNameList(ref comboCount, ref comboNames));
        ApiGuard.Arrays("RespCombo.GetNameList", comboCount, comboNames);
        List<string> selectedCombos = [];
        foreach (var name in comboNames)
        {
            bool selected = false;
            guard.Call($"Results.Setup.GetComboSelectedForOutput:{name}",
                () => setup.GetComboSelectedForOutput(name, ref selected));
            if (selected) selectedCombos.Add(name);
        }

        List<ExtractionIssue> issues = [];
        var limitations = new[] { selectedCases.Count + selectedCombos.Count == 0
            ? "No cases or combinations are selected for output in ETABS. Results may be unavailable for this selection. The inspector does not change the selection."
            : $"Results use the existing ETABS output selection: {selectedCases.Count} cases and {selectedCombos.Count} combinations. Unselected results and result freshness are not verified." };

        var drifts = Read("StoryDrifts", "Results.StoryDrifts", () =>
        {
            int n = 0; string[] story = [], cases = [], stepTypes = [], directions = [], labels = [];
            double[] steps = [], drift = [], x = [], y = [], z = [];
            guard.Call("Results.StoryDrifts", () => results.StoryDrifts(ref n, ref story, ref cases,
                ref stepTypes, ref steps, ref directions, ref drift, ref labels, ref x, ref y, ref z));
            ApiGuard.Arrays("Results.StoryDrifts", n, story, cases, stepTypes, steps, directions, drift, labels, x, y, z);
            return Enumerable.Range(0, n).Select(i => new StoryDriftResult(story[i], cases[i], stepTypes[i],
                ApiGuard.Finite("Results.StoryDrifts", steps[i]), directions[i], ApiGuard.Finite("Results.StoryDrifts", drift[i]),
                labels[i], ApiGuard.Finite("Results.StoryDrifts", x[i]), ApiGuard.Finite("Results.StoryDrifts", y[i]),
                ApiGuard.Finite("Results.StoryDrifts", z[i]))).ToArray();
        });
        if (drifts.Items.Count > 0)
        {
            // Live comparison shows StoryDrifts.X/Y/Z are coordinates. Retrieve actual
            // displacements independently, matching the exact point, case and result step.
            var points = Read("StoryPointDisplacements", "Results.JointDrifts", () =>
            {
                int n = 0;
                string[] stories = [], labels = [], names = [], cases = [], types = [];
                double[] steps = [], dx = [], dy = [], driftX = [], driftY = [];
                guard.Call("Results.JointDrifts", () => results.JointDrifts(ref n, ref stories, ref labels,
                    ref names, ref cases, ref types, ref steps, ref dx, ref dy, ref driftX, ref driftY));
                ApiGuard.Arrays("Results.JointDrifts", n, stories, labels, names, cases, types, steps, dx, dy, driftX, driftY);
                return Enumerable.Range(0, n).Select(i => (
                    Key: (stories[i], labels[i], cases[i], types[i], ApiGuard.Finite("Results.JointDrifts", steps[i])),
                    X: ApiGuard.Finite("Results.JointDrifts", dx[i]), Y: ApiGuard.Finite("Results.JointDrifts", dy[i]))).ToArray();
            });
            var byPoint = points.Items.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.ToArray());
            drifts = Data(drifts.Items.Select(row => row with
            {
                Displacement = byPoint.TryGetValue((row.Story, row.Label, row.Case, row.StepType, row.StepNumber), out var match)
                    && match.Length == 1 ? row.Direction switch { "X" => match[0].X, "Y" => match[0].Y, _ => (double?)null } : null
            }), ["Results.StoryDrifts", "Results.JointDrifts"], limitations.Concat(points.Limitations));
        }
        var modal = Read("ModalParticipation", "Results.ModalParticipatingMassRatios", () =>
        {
            int n = 0; string[] cases = [], stepTypes = [];
            double[] step = [], period = [], ux = [], uy = [], uz = [], sumUx = [], sumUy = [], sumUz = [],
                rx = [], ry = [], rz = [], sumRx = [], sumRy = [], sumRz = [];
            guard.Call("Results.ModalParticipatingMassRatios", () => results.ModalParticipatingMassRatios(ref n,
                ref cases, ref stepTypes, ref step, ref period, ref ux, ref uy, ref uz,
                ref sumUx, ref sumUy, ref sumUz, ref rx, ref ry, ref rz, ref sumRx, ref sumRy, ref sumRz));
            ApiGuard.Arrays("Results.ModalParticipatingMassRatios", n, cases, stepTypes, step, period, ux, uy, uz,
                sumUx, sumUy, sumUz, rx, ry, rz, sumRx, sumRy, sumRz);
            return Enumerable.Range(0, n).Select(i => new ModalParticipationResult(cases[i], stepTypes[i],
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", step[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", period[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", ux[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", uy[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", uz[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumUx[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumUy[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumUz[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", rx[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", ry[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", rz[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumRx[i]),
                ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumRy[i]), ApiGuard.Finite("Results.ModalParticipatingMassRatios", sumRz[i]))).ToArray();
        });
        var reactions = Read("BaseReactions", "Results.BaseReact", () =>
        {
            int n = 0; string[] cases = [], stepTypes = [];
            double[] step = [], fx = [], fy = [], fz = [], mx = [], my = [], mz = [];
            double gx = 0, gy = 0, gz = 0;
            guard.Call("Results.BaseReact", () => results.BaseReact(ref n, ref cases, ref stepTypes,
                ref step, ref fx, ref fy, ref fz, ref mx, ref my, ref mz, ref gx, ref gy, ref gz));
            ApiGuard.Arrays("Results.BaseReact", n, cases, stepTypes, step, fx, fy, fz, mx, my, mz);
            var point = Position("Results.BaseReact", gx, gy, gz);
            return Enumerable.Range(0, n).Select(i => new BaseReactionResult(cases[i], stepTypes[i],
                ApiGuard.Finite("Results.BaseReact", step[i]), ApiGuard.Finite("Results.BaseReact", fx[i]),
                ApiGuard.Finite("Results.BaseReact", fy[i]), ApiGuard.Finite("Results.BaseReact", fz[i]),
                ApiGuard.Finite("Results.BaseReact", mx[i]), ApiGuard.Finite("Results.BaseReact", my[i]),
                ApiGuard.Finite("Results.BaseReact", mz[i]), point)).ToArray();
        });
        int tableCount = 0; string[] tableKeys = [], tableNames = []; int[] importTypes = []; bool[] empty = [];
        guard.Call("DatabaseTables.GetAllTables:AnalysisResults", () => tableApi.GetAllTables(
            ref tableCount, ref tableKeys, ref tableNames, ref importTypes, ref empty));
        ApiGuard.Arrays("DatabaseTables.GetAllTables:AnalysisResults", tableCount, tableKeys, tableNames, importTypes, empty);
        var resultTableSnapshots = new List<AnalysisTableSnapshot>();
        foreach (var index in Enumerable.Range(0, tableCount).Where(i =>
                     string.Join(" ", tableKeys[i], tableNames[i]).Contains("drift", StringComparison.OrdinalIgnoreCase) ||
                     string.Join(" ", tableKeys[i], tableNames[i]).Contains("modal participating mass", StringComparison.OrdinalIgnoreCase) ||
                     string.Join(" ", tableKeys[i], tableNames[i]).Contains("base reaction", StringComparison.OrdinalIgnoreCase)))
        {
            if (empty[index])
            {
                resultTableSnapshots.Add(new(tableKeys[index], tableNames[index], true, [], 0));
                continue;
            }
            try
            {
                var table = ReadTable(tableKeys[index]);
                resultTableSnapshots.Add(new(table.Key, tableNames[index], false, table.Fields, table.Rows.Count));
            }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Analysis result table extraction failed for {Table}", tableKeys[index]);
                issues.Add(new($"ResultTable:{tableKeys[index]}", ex.Message, ex.Operation, ex.ReturnCode));
                resultTableSnapshots.Add(new(tableKeys[index], tableNames[index], false, [], 0));
            }
        }
        var modalTable = resultTableSnapshots.FirstOrDefault(t => !t.IsEmpty && t.Rows > 0 &&
            t.Name.Contains("Modal Participating Mass Ratios", StringComparison.OrdinalIgnoreCase));
        if (modal.Items.Count == 0 && modalTable is not null)
        {
            try
            {
                var table = ReadTable(modalTable.Key);
                var modalRows = ModalParticipationTable.Parse(table);
                modal = Data(modalRows, [$"DatabaseTables.GetTableForDisplayArray:{modalTable.Key}"],
                    ["Modal results recovered from the verified table schema; the direct API failure is retained in Issues. Table display values are rounded."]);

            }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Modal result table fallback failed for {Table}", modalTable.Key);
                issues.Add(new("ModalParticipationTableFallback", ex.Message, ex.Operation, ex.ReturnCode));
            }
        }
        var storyForces = Read("StoryForces", "DatabaseTables.GetTableForDisplayArray:Story Forces",
            () => StoryForceTable.Parse(ReadTable("Story Forces")));
        return new(drifts, modal, reactions, selectedCases, selectedCombos, resultTableSnapshots, issues)
            { StoryForces = storyForces, Extended = ReadExtendedAnalysis(), VerificationEvidence = ReadVerificationEvidence() };

        DataSet<T> Read<T>(string component, string operation, Func<IReadOnlyList<T>> read)
        {
            try { return Data(read(), [operation], limitations); }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Analysis result extraction failed for {Component}", component);
                issues.Add(new(component, ex.Message, ex.Operation, ex.ReturnCode));
                return Data(Array.Empty<T>(), [operation], limitations.Append(ex.Message).Append("A failed API read does not prove that results are missing. ETABS output selection was left unchanged.")) with { ReadSucceeded = false };
            }
        }
    }

    public AnalysisDefinitionData ReadAnalysisDefinitions()
    {
        Check();
        int count = 0; string[] names = [];
        guard.Call("LoadCases.GetNameList", () => model.LoadCases.GetNameList(ref count, ref names));
        ApiGuard.Arrays("LoadCases.GetNameList", count, names);
        List<ResponseSpectrumCase> spectrumCases = [];
        List<GeometricNonlinearitySetting> nonlinearSettings = [];
        var responseApi = Own(model.LoadCases.ResponseSpectrum);
        var nonlinearApi = Own(model.LoadCases.StaticNonlinear);
        foreach (var name in names)
        {
            eLoadCaseType type = default; int subtype = 0;
            guard.Call($"LoadCases.GetTypeOAPI:{name}", () => model.LoadCases.GetTypeOAPI(name, ref type, ref subtype));
            if (type == eLoadCaseType.ResponseSpectrum)
            {
                int n = 0; string[] directions = [], functions = [], systems = []; double[] factors = [], angles = [];
                guard.Call($"ResponseSpectrum.GetLoads:{name}", () => responseApi.GetLoads(name, ref n,
                    ref directions, ref functions, ref factors, ref systems, ref angles));
                ApiGuard.Arrays("ResponseSpectrum.GetLoads", n, directions, functions, factors, systems, angles);
                string modalCase = ""; int modalType = 0, periodicType = 0, directionType = 0;
                double f1 = 0, f2 = 0, td = 0, directionValue = 0;
                guard.Call($"ResponseSpectrum.GetModalCase:{name}", () => responseApi.GetModalCase(name, ref modalCase));
                guard.Call($"ResponseSpectrum.GetModalComb_1:{name}", () => responseApi.GetModalComb_1(name,
                    ref modalType, ref f1, ref f2, ref periodicType, ref td));
                guard.Call($"ResponseSpectrum.GetDirComb:{name}", () => responseApi.GetDirComb(name, ref directionType, ref directionValue));
                var loads = Enumerable.Range(0, n).Select(i => new ResponseSpectrumLoad(directions[i], functions[i],
                    ApiGuard.Finite("ResponseSpectrum.GetLoads", factors[i]), systems[i],
                    ApiGuard.Finite("ResponseSpectrum.GetLoads", angles[i]))).ToArray();
                spectrumCases.Add(new(name, modalCase, modalType, ApiGuard.Finite("ResponseSpectrum.GetModalComb_1", f1),
                    ApiGuard.Finite("ResponseSpectrum.GetModalComb_1", f2), periodicType,
                    ApiGuard.Finite("ResponseSpectrum.GetModalComb_1", td), directionType,
                    ApiGuard.Finite("ResponseSpectrum.GetDirComb", directionValue), loads));
            }
            else if (type == eLoadCaseType.NonlinearStatic)
            {
                int option = 0;
                guard.Call($"StaticNonlinear.GetGeometricNonlinearity:{name}",
                    () => nonlinearApi.GetGeometricNonlinearity(name, ref option));
                string? meaning = option switch { 0 => "None", 1 => "P-Delta", 2 => "P-Delta plus large displacements", _ => null };
                nonlinearSettings.Add(new(name, type.ToString(), option, meaning));
            }
        }
        var operations = new[] { "LoadCases.GetNameList", "LoadCases.GetTypeOAPI", "ResponseSpectrum.GetLoads",
            "ResponseSpectrum.GetModalCase", "ResponseSpectrum.GetModalComb_1", "ResponseSpectrum.GetDirComb",
            "StaticNonlinear.GetGeometricNonlinearity" };
        var limitation = new[] { "Only regular nonlinear static cases are queried for geometric nonlinearity. Nonlinear staged and direct-integration history cases are not yet extracted. Raw combination option codes are retained; detailed interpretation is not verified." };
        return new(Data(spectrumCases, operations, limitation), Data(nonlinearSettings, operations, limitation));
    }

    private T Own<T>(T reference) where T : class
    {
        if (!references.Any(r => ReferenceEquals(r, reference))) references.Add(reference);
        return reference;
    }
    private void Check() { ObjectDisposedException.ThrowIf(disposed, this); guard.CheckThread(); }
    private static Coordinate Position(string operation, double x, double y, double z) =>
        new(ApiGuard.Finite(operation, x), ApiGuard.Finite(operation, y), ApiGuard.Finite(operation, z));
    internal static string NormalizeDesignMessage(string? message) => string.IsNullOrWhiteSpace(message) ||
        message.Trim().Equals("No Message", StringComparison.OrdinalIgnoreCase) ? "" : message;

    private DataSet<T> Data<T>(IEnumerable<T> items, string[] operations, IEnumerable<string>? limitations = null) =>
        new(items.ToArray(), operations.Select(o => new SourceEvidence(o, DateTimeOffset.UtcNow, units)).ToArray(),
            limitations?.ToArray() ?? []);

    public ModelIdentity ReadIdentity()
    {
        Check();
        string version = ""; double internalVersion = 0;
        guard.Call("SapModel.GetVersion", () => model.GetVersion(ref version, ref internalVersion));
        var path = guard.Read("SapModel.GetModelFilename", () => model.GetModelFilename(true));
        if (string.IsNullOrWhiteSpace(path))
            throw new ConnectionException("ETABS has no identifiable model file. Open a named model; the inspector will not save it.");
        var locked = guard.Read("SapModel.GetModelIsLocked", model.GetModelIsLocked);
        eForce force = default; eLength length = default; eTemperature temperature = default;
        guard.Call("SapModel.GetPresentUnits_2", () => model.GetPresentUnits_2(ref force, ref length, ref temperature));
        units = Units(force, length, temperature);
        guard.Call("SapModel.GetDatabaseUnits_2", () => model.GetDatabaseUnits_2(ref force, ref length, ref temperature));
        return new(version, path, locked, units, Units(force, length, temperature), DateTimeOffset.UtcNow);
    }

    private static UnitContext Units(eForce force, eLength length, eTemperature temperature)
    {
        if (!Enum.IsDefined(force) || !Enum.IsDefined(length) || !Enum.IsDefined(temperature) ||
            (int)force == 0 || (int)length == 0 || (int)temperature == 0)
            throw new ApiException("Units", null, "Unknown ETABS unit definition");
        return new(force.ToString(), length.ToString(), temperature.ToString());
    }

    public StoryData ReadStories()
    {
        Check();
        int towers = 0; string[] towerNames = [];
        guard.Call("Tower.GetNameList", () => towerApi.GetNameList(ref towers, ref towerNames));
        ApiGuard.Arrays("Tower.GetNameList", towers, towerNames);
        if (towers != 1)
            throw new ApiException("Tower.GetNameList", null, "Phase 1 story extraction requires a single tower. Multi-tower traversal is not verified; no active tower will be changed.");
        const string op = "Story.GetStories_2";
        double baseElevation = 0; int count = 0;
        string[] names = [], similar = []; double[] elevations = [], heights = [], spliceHeights = [];
        bool[] masters = [], spliceAbove = []; int[] colors = [];
        guard.Call(op, () => storyApi.GetStories_2(ref baseElevation, ref count, ref names, ref elevations,
            ref heights, ref masters, ref similar, ref spliceAbove, ref spliceHeights, ref colors));
        ApiGuard.Arrays(op, count, names, elevations, heights, masters, similar, spliceAbove, spliceHeights, colors);
        return new(ApiGuard.Finite(op, baseElevation), Enumerable.Range(0, count)
            .Select(i => new Story(names[i], ApiGuard.Finite(op, elevations[i]), ApiGuard.Finite(op, heights[i]), masters[i], similar[i]))
            .OrderBy(s => s.Elevation).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray())
            { Source = new(op, DateTimeOffset.UtcNow, units) };
    }

    public DataSet<Joint> ReadJoints()
    {
        Check();
        const string op = "PointObj.GetAllPoints";
        int count = 0; string[] names = []; double[] x = [], y = [], z = [];
        guard.Call(op, () => pointApi.GetAllPoints(ref count, ref names, ref x, ref y, ref z, "Global"));
        ApiGuard.Arrays(op, count, names, x, y, z);
        return Data(Enumerable.Range(0, count).Select(i => new Joint(names[i], Position(op, x[i], y[i], z[i])))
            .OrderBy(j => j.Id, StringComparer.Ordinal), [op]);
    }

    public DataSet<Frame> ReadFrames()
    {
        Check();
        const string op = "FrameObj.GetAllFrames";
        int count = 0; string[] names = [], sections = [], stories = [], p1 = [], p2 = [];
        double[] x1 = [], y1 = [], z1 = [], x2 = [], y2 = [], z2 = [], angles = [],
            offset1X = [], offset2X = [], offset1Y = [], offset2Y = [], offset1Z = [], offset2Z = [];
        int[] cardinal = [];
        guard.Call(op, () => frameApi.GetAllFrames(ref count, ref names, ref sections, ref stories, ref p1, ref p2,
            ref x1, ref y1, ref z1, ref x2, ref y2, ref z2, ref angles, ref offset1X, ref offset2X,
            ref offset1Y, ref offset2Y, ref offset1Z, ref offset2Z, ref cardinal, "Global"));
        ApiGuard.Arrays(op, count, names, sections, stories, p1, p2, x1, y1, z1, x2, y2, z2, angles);
        return Data(Enumerable.Range(0, count).Select(i => new Frame(names[i], stories[i], sections[i], p1[i], p2[i],
            Position(op, x1[i], y1[i], z1[i]), Position(op, x2[i], y2[i], z2[i]), ApiGuard.Finite(op, angles[i])))
            .OrderBy(f => f.Id, StringComparer.Ordinal), [op]);
    }

    public DataSet<Area> ReadAreas()
    {
        Check();
        int count = 0; string[] names = [];
        guard.Call("AreaObj.GetNameList", () => areaApi.GetNameList(ref count, ref names));
        ApiGuard.Arrays("AreaObj.GetNameList", count, names);
        var areas = new List<Area>();
        // GetAllAreas delimiters are undocumented locally; use documented point lists until validated.
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            int n = 0; string[] points = []; string label = "", story = "", section = "";
            bool opening = false; eAreaDesignOrientation orientation = default;
            guard.Call("AreaObj.GetPoints", () => areaApi.GetPoints(name, ref n, ref points));
            ApiGuard.Arrays("AreaObj.GetPoints", n, points);
            guard.Call("AreaObj.GetLabelFromName", () => areaApi.GetLabelFromName(name, ref label, ref story));
            guard.Call("AreaObj.GetProperty", () => areaApi.GetProperty(name, ref section));
            guard.Call("AreaObj.GetOpening", () => areaApi.GetOpening(name, ref opening));
            guard.Call("AreaObj.GetDesignOrientation", () => areaApi.GetDesignOrientation(name, ref orientation));
            areas.Add(new(name, label, story, section, orientation.ToString(), opening, points));
        }
        return Data(areas, ["AreaObj.GetNameList", "AreaObj.GetPoints", "AreaObj.GetLabelFromName",
            "AreaObj.GetProperty", "AreaObj.GetOpening", "AreaObj.GetDesignOrientation"]);
    }

    public DataSet<Material> ReadMaterials()
    {
        Check();
        int count = 0; string[] names = [];
        guard.Call("PropMaterial.GetNameList", () => materialApi.GetNameList(ref count, ref names));
        ApiGuard.Arrays("PropMaterial.GetNameList", count, names);
        List<Material> materials = []; List<string> limitations = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            eMatType type = default; int symmetry = 0;
            guard.Call("PropMaterial.GetTypeOAPI", () => materialApi.GetTypeOAPI(name, ref type, ref symmetry));
            double weight = 0, mass = 0, e = 0, poisson = 0, alpha = 0, g = 0;
            guard.Call("PropMaterial.GetWeightAndMass", () => materialApi.GetWeightAndMass(name, ref weight, ref mass));
            double? modulus = null, ratio = null, fc = null, fy = null, fu = null;
            if (symmetry == 0)
            {
                guard.Call("PropMaterial.GetMPIsotropic", () => materialApi.GetMPIsotropic(name, ref e, ref poisson, ref alpha, ref g));
                modulus = ApiGuard.Finite("PropMaterial.GetMPIsotropic", e);
                ratio = ApiGuard.Finite("PropMaterial.GetMPIsotropic", poisson);
            }
            else if (symmetry == 3)
            {
                guard.Call("PropMaterial.GetMPUniaxial", () => materialApi.GetMPUniaxial(name, ref e, ref alpha));
                modulus = ApiGuard.Finite("PropMaterial.GetMPUniaxial", e);
            }
            else limitations.Add($"{name}: anisotropic/orthotropic elastic properties are not extracted in Phase 1.");
            if (type == eMatType.Concrete)
            {
                double strength = 0, factor = 0, strain = 0, ultimate = 0, friction = 0, dilation = 0;
                bool lightweight = false; int curve = 0, hysteresis = 0;
                guard.Call("PropMaterial.GetOConcrete", () => materialApi.GetOConcrete(name, ref strength, ref lightweight,
                    ref factor, ref curve, ref hysteresis, ref strain, ref ultimate, ref friction, ref dilation));
                fc = ApiGuard.Finite("PropMaterial.GetOConcrete", strength);
            }
            if (type == eMatType.Rebar)
            {
                double yield = 0, tensile = 0, expectedYield = 0, expectedTensile = 0, strain = 0, ultimate = 0;
                int curve = 0, hysteresis = 0; bool caltrans = false;
                guard.Call("PropMaterial.GetORebar", () => materialApi.GetORebar(name, ref yield, ref tensile,
                    ref expectedYield, ref expectedTensile, ref curve, ref hysteresis, ref strain, ref ultimate, ref caltrans));
                fy = ApiGuard.Finite("PropMaterial.GetORebar", yield);
                fu = ApiGuard.Finite("PropMaterial.GetORebar", tensile);
            }
            materials.Add(new(name, type.ToString(), symmetry, ApiGuard.Finite("PropMaterial.GetWeightAndMass", weight),
                ApiGuard.Finite("PropMaterial.GetWeightAndMass", mass), modulus, ratio, fc, fy, fu));
        }
        return Data(materials, ["PropMaterial.GetNameList", "PropMaterial.GetTypeOAPI", "PropMaterial.GetWeightAndMass",
            "PropMaterial.GetMPIsotropic / GetMPUniaxial", "PropMaterial.GetOConcrete / GetORebar (where applicable)"], limitations);
    }

    public DataSet<FrameSection> ReadFrameSections()
    {
        Check();
        int count = 0; string[] names = [];
        guard.Call("PropFrame.GetNameList", () => framePropertyApi.GetNameList(ref count, ref names));
        ApiGuard.Arrays("PropFrame.GetNameList", count, names);
        List<FrameSection> sections = []; List<string> limitations = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            eFramePropType type = default;
            guard.Call("PropFrame.GetTypeOAPI", () => framePropertyApi.GetTypeOAPI(name, ref type));
            string file = "", material = "", notes = "", guid = ""; int color = 0;
            double depth = 0, width = 0, diameter = 0; string? limitation = null;
            var extra = new Dictionary<string, double>();
            string[] autoSections = []; string autoStart = "";
            if (type == eFramePropType.Rectangular)
                guard.Call("PropFrame.GetRectangle", () => framePropertyApi.GetRectangle(name, ref file, ref material,
                    ref depth, ref width, ref color, ref notes, ref guid));
            else if (type == eFramePropType.Circle)
            {
                guard.Call("PropFrame.GetCircle", () => framePropertyApi.GetCircle(name, ref file, ref material,
                    ref diameter, ref color, ref notes, ref guid));
                limitation = $"{name}: circular section extraction has not yet been compared with a representative ETABS 22.7.0 model.";
                limitations.Add(limitation);
            }
            else if (type == eFramePropType.I)
            {
                double tf = 0, tw = 0, bottomWidth = 0, bottomThickness = 0;
                guard.Call("PropFrame.GetISection", () => framePropertyApi.GetISection(name, ref file, ref material,
                    ref depth, ref width, ref tf, ref tw, ref bottomWidth, ref bottomThickness, ref color, ref notes, ref guid));
                extra["TopFlangeThickness"] = ApiGuard.Finite("PropFrame.GetISection", tf);
                extra["WebThickness"] = ApiGuard.Finite("PropFrame.GetISection", tw);
                extra["BottomFlangeWidth"] = ApiGuard.Finite("PropFrame.GetISection", bottomWidth);
                extra["BottomFlangeThickness"] = ApiGuard.Finite("PropFrame.GetISection", bottomThickness);
            }
            else if (type == eFramePropType.Auto)
            {
                int number = 0;
                guard.Call("PropFrame.GetAutoSelectSteel", () => framePropertyApi.GetAutoSelectSteel(name,
                    ref number, ref autoSections, ref autoStart, ref notes, ref guid));
                ApiGuard.Arrays("PropFrame.GetAutoSelectSteel", number, autoSections);
            }
            else
            {
                limitation = $"{name}: {type} dimensions are not interpreted by the Phase 1 RC extractor.";
                limitations.Add(limitation);
                // Auto-select and nonprismatic sections need their constituent properties, not a guessed material.
                if (type is not (eFramePropType.Auto or eFramePropType.Variable))
                    guard.Call("PropFrame.GetMaterial", () => framePropertyApi.GetMaterial(name, ref material));
            }
            sections.Add(new(name, type.ToString(), string.IsNullOrEmpty(material) ? null : material,
                type is eFramePropType.Rectangular or eFramePropType.I ? ApiGuard.Finite("PropFrame section depth", depth) : null,
                type is eFramePropType.Rectangular or eFramePropType.I ? ApiGuard.Finite("PropFrame section width", width) : null,
                type == eFramePropType.Circle ? ApiGuard.Finite("PropFrame.GetCircle", diameter) : null, limitation)
                { AdditionalDimensions = extra, AutoSelectSections = autoSections, AutoStartSection = type == eFramePropType.Auto ? autoStart : null });
        }
        return Data(sections, ["PropFrame.GetNameList", "PropFrame.GetTypeOAPI", "PropFrame.GetRectangle / GetCircle / GetISection / GetAutoSelectSteel / GetMaterial (where applicable)"], limitations);
    }

    public DataSet<AreaSection> ReadAreaSections()
    {
        Check();
        const string key = "Area Section Property Definitions - Summary";
        var summary = ReadTable(key);
        summary.RequireFields("Name", "Type", "AnalType");
        List<AreaSection> sections = []; List<string> limitations = [];
        foreach (var row in summary.Rows.OrderBy(r => r["Name"], StringComparer.Ordinal))
        {
            string name = row["Name"], kind = row["Type"], material = "", notes = "", guid = "";
            double thickness = 0; int color = 0; eShellType shell = default; string? limitation = null;
            var extra = new Dictionary<string, double>();
            if (kind == "Slab")
            {
                eSlabType type = default;
                guard.Call("PropArea.GetSlab", () => areaPropertyApi.GetSlab(name, ref type, ref shell, ref material,
                    ref thickness, ref color, ref notes, ref guid));
                if (type is eSlabType.Ribbed or eSlabType.Waffle)
                    limitation = $"{name}: rib geometry is not extracted; membrane thickness is not the overall depth.";
            }
            else if (kind == "Wall")
            {
                eWallPropType type = default;
                guard.Call("PropArea.GetWall", () => areaPropertyApi.GetWall(name, ref type, ref shell, ref material,
                    ref thickness, ref color, ref notes, ref guid));
                if (type == eWallPropType.AutoSelectList) limitation = $"{name}: wall auto-select constituents are not extracted.";
            }
            else if (kind == "Deck")
            {
                eDeckType type = default;
                guard.Call("PropArea.GetDeck", () => areaPropertyApi.GetDeck(name, ref type, ref shell, ref material,
                    ref thickness, ref color, ref notes, ref guid));
                if (type == eDeckType.Filled)
                {
                    double slab = 0, rib = 0, top = 0, bottom = 0, spacing = 0, shearThickness = 0,
                        weight = 0, studDiameter = 0, studHeight = 0, studStrength = 0;
                    guard.Call("PropArea.GetDeckFilled", () => areaPropertyApi.GetDeckFilled(name, ref slab, ref rib,
                        ref top, ref bottom, ref spacing, ref shearThickness, ref weight, ref studDiameter, ref studHeight, ref studStrength));
                    foreach (var (keyName, value) in new[] { ("SlabDepth", slab), ("RibDepth", rib), ("RibWidthTop", top),
                        ("RibWidthBottom", bottom), ("RibSpacing", spacing), ("DeckShearThickness", shearThickness) })
                        extra[keyName] = ApiGuard.Finite("PropArea.GetDeckFilled", value);
                }
                else limitation = $"{name}: {type} deck geometry is not yet validated.";
            }
            else limitation = $"{name}: {kind} geometry is outside the Phase 1 RC slab/wall extractor.";
            if (shell == eShellType.Layered) limitation = $"{name}: layered shell material/thickness require layer-by-layer extraction.";
            if (limitation is not null) limitations.Add(limitation);
            var hasHomogeneousSection = (kind is "Slab" or "Wall" or "Deck") && shell != eShellType.Layered &&
                !string.IsNullOrEmpty(material);
            sections.Add(new(name, kind, shell == default ? row["AnalType"] : shell.ToString(),
                hasHomogeneousSection ? material : null,
                hasHomogeneousSection ? ApiGuard.Finite("PropArea thickness", thickness) : null, limitation)
                { AdditionalDimensions = extra });
        }
        return Data(sections, [$"DatabaseTables.GetTableForDisplayArray:{key}", "PropArea.GetSlab / GetWall / GetDeck / GetDeckFilled (where applicable)"], limitations);
    }

    public void Dispose()
    {
        if (disposed) return;
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Release ETABS references on their owning thread.");
        disposed = true;
        foreach (var reference in references.AsEnumerable().Reverse())
        {
            if (!Marshal.IsComObject(reference)) continue;
            try { Marshal.ReleaseComObject(reference); }
            catch (InvalidComObjectException ex) { logger.LogWarning(ex, "An owned COM reference had already disconnected"); }
        }
        references.Clear();
        logger.LogInformation("Released inspector references; ETABS remains running");
    }
}

// Source: ExtendedAnalysisExtraction.cs
public sealed partial class EtabsSession
{
    private ExtendedAnalysisEvidence ReadExtendedAnalysis()
    {
        List<ExtractionIssue> issues=[]; List<AnalysisEvidenceTable> tables=[];
        string[] keys=["Analysis Messages","Joint Displacements","Modal Periods And Frequencies",
            "Story Stiffness","Stiffness Gravity Ratios","Diaphragm Max Over Avg Drifts",
            "Load Pattern Definitions - Auto Seismic - ASCE 7-16","Mass Summary by Story",
            "Mass Summary by Group","Diaphragm Center Of Mass Displacements",
            "Concrete Frame Design Load Combination Data","Pier Forces"];
        foreach(var key in keys)
        {
            try { var t=ReadTable(key); tables.Add(new(key,t.Fields,t.Rows)); }
            catch(ApiException ex)
            {
                tables.Add(new(key,[],[],ex.Message));
                // Pier Forces is optional for the core analysis result set; its absence only prevents SAN-006.
                if (key != "Pier Forces") issues.Add(new("Extended:"+key,ex.Message,ex.Operation,ex.ReturnCode));
            }
        }
        var modifiers=new List<ModifierEvidence>();
        ReadModifiers("FrameObj",frameApi.GetNameList,frameApi.GetModifiers,8);
        ReadModifiers("AreaObj",areaApi.GetNameList,areaApi.GetModifiers,10);
        ReadModifiers("PropFrame",(ref int n,ref string[] names)=>framePropertyApi.GetNameList(ref n,ref names),framePropertyApi.GetModifiers,8);
        ReadModifiers("PropArea",(ref int n,ref string[] names)=>areaPropertyApi.GetNameList(ref n,ref names),areaPropertyApi.GetModifiers,10);
        List<ModeShapeEvidence> modes=[]; List<FrameForceEvidence> forces=[];
        Try("Results.ModeShape",()=> {
            int n=0; string[] obj=[],elm=[],cases=[],types=[]; double[] steps=[],u1=[],u2=[],u3=[],r1=[],r2=[],r3=[];
            guard.Call("Results.ModeShape",()=>model.Results.ModeShape("All",eItemTypeElm.GroupElm,ref n,ref obj,ref elm,ref cases,ref types,ref steps,ref u1,ref u2,ref u3,ref r1,ref r2,ref r3));
            ApiGuard.Arrays("ModeShape",n,obj,elm,cases,types,steps,u1,u2,u3,r1,r2,r3);
            foreach(var v in steps.Concat(u1).Concat(u2).Concat(u3).Concat(r1).Concat(r2).Concat(r3)) ApiGuard.Finite("ModeShape",v);
            modes.AddRange(Enumerable.Range(0,n).Select(i=>new ModeShapeEvidence(obj[i],elm[i],cases[i],types[i],steps[i],u1[i],u2[i],u3[i],r1[i],r2[i],r3[i])));
        });
        Try("Results.FrameForce",()=> {
            int n=0; string[] obj=[],elm=[],cases=[],types=[]; double[] sta=[],es=[],steps=[],p=[],v2=[],v3=[],t=[],m2=[],m3=[];
            guard.Call("Results.FrameForce",()=>model.Results.FrameForce("All",eItemTypeElm.GroupElm,ref n,ref obj,ref sta,ref elm,ref es,ref cases,ref types,ref steps,ref p,ref v2,ref v3,ref t,ref m2,ref m3));
            ApiGuard.Arrays("FrameForce",n,obj,sta,elm,es,cases,types,steps,p,v2,v3,t,m2,m3);
            foreach(var v in sta.Concat(es).Concat(steps).Concat(p).Concat(v2).Concat(v3).Concat(t).Concat(m2).Concat(m3)) ApiGuard.Finite("FrameForce",v);
            forces.AddRange(Enumerable.Range(0,n).Select(i=>new FrameForceEvidence(obj[i],sta[i],elm[i],es[i],cases[i],types[i],steps[i],p[i],v2[i],v3[i],t[i],m2[i],m3[i])));
        });
        string? code=null; bool? available=null;
        Try("DesignConcrete.GetCode",()=> {string value="";guard.Call("DesignConcrete.GetCode",()=>model.DesignConcrete.GetCode(ref value));code=value;});
        Try("DesignConcrete.GetResultsAvailable",()=>available=guard.Read("DesignConcrete.GetResultsAvailable",()=>model.DesignConcrete.GetResultsAvailable()));
        return new(tables,modifiers,modes,forces,code,available,issues);
        void Try(string operation,Action read) {try {read();}catch(ApiException ex){issues.Add(new(operation,ex.Message,ex.Operation,ex.ReturnCode));}}
        void ReadModifiers(string kind,NameList list,ModifierGetter get,int count)
        {
            Try(kind+".GetModifiers",()=> {
                int n=0;string[] names=[];guard.Call(kind+".GetNameList",()=>list(ref n,ref names));ApiGuard.Arrays(kind,n,names);
                foreach(var name in names.Order(StringComparer.Ordinal))
                {double[] values=[];guard.Call(kind+".GetModifiers:"+name,()=>get(name,ref values));ApiGuard.Arrays(kind,count,values);
                 foreach(var v in values)ApiGuard.Finite(kind,v);modifiers.Add(new(kind,name,values));}
            });
        }
    }
    private delegate int NameList(ref int count,ref string[] names);
    private delegate int ModifierGetter(string name,ref double[] values);
}

// Source: MassSourceTable.cs
public static class MassSourceTable
{
    public static IReadOnlyList<MassSource> Parse(DatabaseTable table)
    {
        table.RequireFields("Name", "IsDefault", "SourceSelf", "SourceAdded", "SourceLoads", "LoadPattern", "Multiplier");
        if (table.Rows.Any(r => string.IsNullOrWhiteSpace(r["Name"])))
            throw new ApiException(table.Key, null, "Mass source row has no source name");
        return table.Rows.GroupBy(r => r["Name"], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            bool Flag(string field)
            {
                // ETABS emits header flags once per source, then blank cells on continuation rows.
                var values = group.Select(r => r[field]).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length != 1 || values[0] is not ("Yes" or "No"))
                    throw new ApiException(table.Key, null, $"Missing, conflicting or unknown {field} flag for {group.Key}");
                return values[0] == "Yes";
            }
            if (group.Any(r => string.IsNullOrEmpty(r["LoadPattern"]) && !string.IsNullOrEmpty(r["Multiplier"])))
                throw new ApiException(table.Key, null, $"Mass multiplier without a load pattern for {group.Key}");
            var loads = group.Where(r => !string.IsNullOrEmpty(r["LoadPattern"])).Select(row =>
            {
                if (!double.TryParse(row["Multiplier"], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) || !double.IsFinite(factor))
                    throw new ApiException(table.Key, null, $"Invalid mass multiplier for {group.Key}/{row["LoadPattern"]}");
                return new MassLoad(row["LoadPattern"], factor);
            }).ToArray();
            return new MassSource(group.Key, Flag("IsDefault"), Flag("SourceSelf"), Flag("SourceAdded"), Flag("SourceLoads"), loads);
        }).ToArray();
    }
}

// Source: ModalParticipationTable.cs
public static class ModalParticipationTable
{
    public static IReadOnlyList<ModalParticipationResult> Parse(DatabaseTable table)
    {
        table.RequireFields("Case", "Mode", "Period", "UX", "UY", "UZ", "SumUX", "SumUY", "SumUZ",
            "RX", "RY", "RZ", "SumRX", "SumRY", "SumRZ");
        double Number(IReadOnlyDictionary<string, string> row, string field)
        {
            if (!double.TryParse(row[field], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ApiException($"DatabaseTables:{table.Key}", null, $"Invalid numeric value in field {field}");
            return ApiGuard.Finite($"DatabaseTables:{table.Key}:{field}", value);
        }
        return table.Rows.Select(row => new ModalParticipationResult(row["Case"], "Mode",
            Number(row, "Mode"), Number(row, "Period"), Number(row, "UX"), Number(row, "UY"), Number(row, "UZ"),
            Number(row, "SumUX"), Number(row, "SumUY"), Number(row, "SumUZ"), Number(row, "RX"), Number(row, "RY"),
            Number(row, "RZ"), Number(row, "SumRX"), Number(row, "SumRY"), Number(row, "SumRZ"))).ToArray();
    }
}

// Source: StaRunner.cs
public static class StaRunner
{
    // One serialized session per invocation. Attach, use and release stay on this thread.
    // Cancellation never interrupts a running COM call or terminates ETABS.
    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { Name = "ETABS read-only session", IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

// Source: StoryForceTable.cs
public static class StoryForceTable
{
    public static IReadOnlyList<StoryForceResult> Parse(DatabaseTable table)
    {
        table.RequireFields("Story", "OutputCase", "StepType", "StepNumber", "Location", "P", "VX", "VY");
        return table.Rows.Select(r => new StoryForceResult(r["Story"], r["OutputCase"],
            string.IsNullOrWhiteSpace(r["StepType"]) ? "Single Value" : r["StepType"],
            string.IsNullOrWhiteSpace(r["StepNumber"]) ? 0 : Number(r["StepNumber"]),
            r["Location"], Number(r["P"]), Number(r["VX"]), Number(r["VY"]))).ToArray();
    }
    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new ApiException("Story Forces table", null, "Invalid or non-finite numeric value.");
}

// Source: ValidationExtraction.cs
public sealed partial class EtabsSession : IValidationSession
{
    private string[] JointNames()
    {
        int n = 0; string[] names = [];
        guard.Call("PointObj.GetNameList", () => pointApi.GetNameList(ref n, ref names));
        ApiGuard.Arrays("PointObj.GetNameList", n, names);
        return (names ?? []).Order(StringComparer.Ordinal).ToArray();
    }
    private static string? AssignmentName(string? value) => string.IsNullOrEmpty(value) || value == "None" ? null : value;

    public DataSet<JointAssignment> ReadJointAssignments()
    {
        Check(); List<JointAssignment> result = [];
        foreach (var name in JointNames())
        {
            bool[] restraints = []; eDiaphragmOption option = default; string diaphragm = "";
            guard.Call($"PointObj.GetRestraint:{name}", () => pointApi.GetRestraint(name, ref restraints));
            ApiGuard.Arrays("PointObj.GetRestraint", 6, restraints);
            guard.Call($"PointObj.GetDiaphragm:{name}", () => pointApi.GetDiaphragm(name, ref option, ref diaphragm));
            if (!Enum.IsDefined(option)) throw new ApiException("PointObj.GetDiaphragm", null, $"Unknown diaphragm option for {name}");
            result.Add(new(name, restraints, option.ToString(), AssignmentName(diaphragm)));
        }
        return Data(result, ["PointObj.GetNameList", "PointObj.GetRestraint", "PointObj.GetDiaphragm"]);
    }
    public DataSet<FrameAssignment> ReadFrameAssignments()
    {
        Check(); int n = 0; string[] names = [];
        var overwrites = ReadMaterialOverwrites("Frame Assignments - Material Overwrites");
        guard.Call("FrameObj.GetNameList", () => frameApi.GetNameList(ref n, ref names));
        ApiGuard.Arrays("FrameObj.GetNameList", n, names);
        List<FrameAssignment> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            string type = "", material = ""; bool[] start = [], end = []; double[] startValues = [], endValues = [];
            guard.Call($"FrameObj.GetTypeOAPI:{name}", () => frameApi.GetTypeOAPI(name, ref type));
            material = overwrites.GetValueOrDefault(name) ?? "";
            guard.Call($"FrameObj.GetReleases:{name}", () => frameApi.GetReleases(name, ref start, ref end, ref startValues, ref endValues));
            ApiGuard.Arrays("FrameObj.GetReleases", 6, start, end, startValues, endValues);
            foreach (var v in startValues.Concat(endValues)) ApiGuard.Finite("FrameObj.GetReleases", v);
            result.Add(new(name, type, AssignmentName(material), start, end, startValues, endValues));
        }
        return Data(result, ["FrameObj.GetNameList", "FrameObj.GetTypeOAPI", "DatabaseTables:Frame Assignments - Material Overwrites", "FrameObj.GetReleases"],
            overwrites.Count == 0 ? [] : ["Nonempty material-overwrite table mapping requires representative live validation; material conclusions remain unverified."]);
    }
    public DataSet<AreaAssignment> ReadAreaAssignments()
    {
        Check(); int n = 0; string[] names = [];
        var overwrites = ReadMaterialOverwrites("Area Assignments - Material Overwrites");
        guard.Call("AreaObj.GetNameList", () => areaApi.GetNameList(ref n, ref names));
        ApiGuard.Arrays("AreaObj.GetNameList", n, names);
        List<AreaAssignment> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            string diaphragm = "", material = "";
            guard.Call($"AreaObj.GetDiaphragm:{name}", () => areaApi.GetDiaphragm(name, ref diaphragm));
            material = overwrites.GetValueOrDefault(name) ?? "";
            result.Add(new(name, AssignmentName(diaphragm), AssignmentName(material)));
        }
        return Data(result, ["AreaObj.GetNameList", "AreaObj.GetDiaphragm", "DatabaseTables:Area Assignments - Material Overwrites"],
            overwrites.Count == 0 ? [] : ["Nonempty material-overwrite table mapping requires representative live validation; material conclusions remain unverified."]);
    }
    public DataSet<LinkConnection> ReadLinks()
    {
        Check(); var api = Own(model.LinkObj); int n = 0; string[] names = [];
        guard.Call("LinkObj.GetNameList", () => api.GetNameList(ref n, ref names));
        ApiGuard.Arrays("LinkObj.GetNameList", n, names);
        List<LinkConnection> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            string p1 = "", p2 = "";
            guard.Call($"LinkObj.GetPoints:{name}", () => api.GetPoints(name, ref p1, ref p2));
            result.Add(new(name, p1, p2));
        }
        return Data(result, ["LinkObj.GetNameList", "LinkObj.GetPoints"], n == 0 ? [] :
            ["Link endpoint acquisition has not been compared with a representative live link model; graph conclusions remain preliminary."]);
    }
    public DataSet<LoadPattern> ReadLoadPatterns()
    {
        Check(); var api = Own(model.LoadPatterns); int n = 0; string[] names = [];
        guard.Call("LoadPatterns.GetNameList", () => api.GetNameList(ref n, ref names));
        ApiGuard.Arrays("LoadPatterns.GetNameList", n, names);
        List<LoadPattern> result = []; List<string> limitations = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            eLoadPatternType type = default; double multiplier = 0;
            guard.Call($"LoadPatterns.GetLoadType:{name}", () => api.GetLoadType(name, ref type));
            guard.Call($"LoadPatterns.GetSelfWTMultiplier:{name}", () => api.GetSelfWTMultiplier(name, ref multiplier));
            string? seismicCode = null;
            if (type == eLoadPatternType.Quake)
            {
                string code = "";
                try
                {
                    guard.Call($"LoadPatterns.GetAutoSeismicCode:{name}", () => api.GetAutoSeismicCode(name, ref code));
                    seismicCode = code;
                }
                catch (ApiException ex) { limitations.Add($"Auto-seismic settings for {name} could not be read: {ex.Message}"); }
            }
            result.Add(new(name, type.ToString(), ApiGuard.Finite("LoadPatterns.GetSelfWTMultiplier", multiplier)) { AutoSeismicCode = seismicCode });
        }
        return Data(result, ["LoadPatterns.GetNameList", "LoadPatterns.GetLoadType", "LoadPatterns.GetSelfWTMultiplier", "LoadPatterns.GetAutoSeismicCode (Quake patterns only)"], limitations);
    }
    public DataSet<LoadCase> ReadLoadCases()
    {
        Check(); var api = Own(model.LoadCases); var staticApi = Own(api.StaticLinear);
        int n = 0; string[] names = [];
        guard.Call("LoadCases.GetNameList", () => api.GetNameList(ref n, ref names));
        ApiGuard.Arrays("LoadCases.GetNameList", n, names);
        List<LoadCase> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            eLoadCaseType type = default; int subType = 0; List<CaseLoad>? loads = null;
            guard.Call($"LoadCases.GetTypeOAPI:{name}", () => api.GetTypeOAPI(name, ref type, ref subType));
            if (type == eLoadCaseType.LinearStatic)
            {
                int count = 0; string[] loadTypes = [], loadNames = []; double[] factors = [];
                guard.Call($"StaticLinear.GetLoads:{name}", () => staticApi.GetLoads(name, ref count, ref loadTypes, ref loadNames, ref factors));
                ApiGuard.Arrays("StaticLinear.GetLoads", count, loadTypes, loadNames, factors);
                loads = Enumerable.Range(0, count).Select(i => new CaseLoad(loadTypes[i], loadNames[i],
                    ApiGuard.Finite("StaticLinear.GetLoads", factors[i]))).ToList();
            }
            result.Add(new(name, type.ToString(), subType, loads));
        }
        return Data(result, ["LoadCases.GetNameList", "LoadCases.GetTypeOAPI", "StaticLinear.GetLoads (linear static only)"]);
    }
    public DataSet<LoadCombination> ReadCombinations()
    {
        Check(); var api = Own(model.RespCombo); int n = 0; string[] names = [];
        guard.Call("RespCombo.GetNameList", () => api.GetNameList(ref n, ref names));
        ApiGuard.Arrays("RespCombo.GetNameList", n, names);
        List<LoadCombination> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            int type = 0, count = 0; eCNameType[] types = []; string[] members = []; int[] modes = []; double[] factors = [];
            guard.Call($"RespCombo.GetTypeCombo:{name}", () => api.GetTypeCombo(name, ref type));
            guard.Call($"RespCombo.GetCaseList_1:{name}", () => api.GetCaseList_1(name, ref count, ref types, ref members, ref modes, ref factors));
            ApiGuard.Arrays("RespCombo.GetCaseList_1", count, types, members, modes, factors);
            var terms = Enumerable.Range(0, count).Select(i => new CombinationTerm(types[i].ToString(), members[i], modes[i],
                ApiGuard.Finite("RespCombo.GetCaseList_1", factors[i]))).ToArray();
            result.Add(new(name, type, terms));
        }
        return Data(result, ["RespCombo.GetNameList", "RespCombo.GetTypeCombo", "RespCombo.GetCaseList_1"]);
    }
    public DataSet<MassSource> ReadMassSources()
    {
        Check();
        var sources = MassSourceTable.Parse(ReadTable("Mass Source Definition"));
        if (sources.Count == 0)
        {
            // An empty definition table does not prove the absence of ETABS's default mass source.
            bool elements = false, added = false, loads = false; int count = 0;
            string[] patterns = []; double[] factors = [];
            guard.Call("PropMaterial.GetMassSource_1", () => materialApi.GetMassSource_1(ref elements, ref added, ref loads, ref count, ref patterns, ref factors));
            if (loads) ApiGuard.Arrays("PropMaterial.GetMassSource_1", count, patterns, factors);
            var contributions = loads ? Enumerable.Range(0, count).Select(i => new MassLoad(patterns[i], ApiGuard.Finite("PropMaterial.GetMassSource_1", factors[i]))).ToArray() : [];
            return Data(new[] { new MassSource("Default source (name unavailable)", true, elements, added, loads, contributions) },
                ["DatabaseTables.GetTableForDisplayArray:Mass Source Definition", "PropMaterial.GetMassSource_1"],
                ["The definition table is empty. Default-source contributions were read from GetMassSource_1; the source name and case-specific source choices are unavailable."]);
        }
        return Data(sources, ["DatabaseTables.GetTableForDisplayArray:Mass Source Definition"], sources.Count <= 1 ? [] :
            ["Multiple mass-source acquisition awaits representative live validation."]);
    }
    public DataSet<Diaphragm> ReadDiaphragms()
    {
        Check(); var api = Own(model.Diaphragm); int n = 0; string[] names = [];
        guard.Call("Diaphragm.GetNameList", () => api.GetNameList(ref n, ref names));
        ApiGuard.Arrays("Diaphragm.GetNameList", n, names);
        List<Diaphragm> result = [];
        foreach (var name in (names ?? []).Order(StringComparer.Ordinal))
        {
            bool semiRigid = false;
            guard.Call($"Diaphragm.GetDiaphragm:{name}", () => api.GetDiaphragm(name, ref semiRigid));
            result.Add(new(name, semiRigid));
        }
        return Data(result, ["Diaphragm.GetNameList", "Diaphragm.GetDiaphragm"]);
    }

    private IReadOnlyDictionary<string, string> ReadMaterialOverwrites(string key)
    {
        int count = 0; string[] keys = [], names = []; int[] importTypes = []; bool[] empty = [];
        guard.Call("DatabaseTables.GetAllTables", () => tableApi.GetAllTables(ref count, ref keys, ref names, ref importTypes, ref empty));
        ApiGuard.Arrays("DatabaseTables.GetAllTables", count, keys, names, importTypes, empty);
        var index = Array.IndexOf(keys, key);
        if (index < 0) throw new ApiException(key, null, "Required assignment table is unavailable");
        if (empty[index]) return new Dictionary<string, string>();
        var table = ReadTable(key); table.RequireFields("UniqueName", "Material");
        if (table.Rows.GroupBy(r => r["UniqueName"]).Any(g => g.Count() != 1))
            throw new ApiException(key, null, "Duplicate material overwrite rows");
        return table.Rows.ToDictionary(r => r["UniqueName"], r => r["Material"], StringComparer.Ordinal);
    }
}

// Source: VerificationAccess.cs
public sealed partial class EtabsSession
{
    // Friend test assembly only. Never exposed to engineering or application code.
    internal cSapModel ModelForVerification { get { Check(); return model; } }
}

