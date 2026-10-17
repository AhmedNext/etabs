
public sealed class Phase2ExtractionTests
{
    private static InspectionExtractor Extractor => new(new(NullLogger<ModelExtractor>.Instance), NullLogger<InspectionExtractor>.Instance);
    [Fact]
    public void ValidationApiFailurePreservesIndependentInputs()
    {
        using var session = new FakeSession { FailMass = true };
        var result = Extractor.Extract(session);
        Assert.Null(result.Validation.MassSources); Assert.NotNull(result.Validation.LoadPatterns); Assert.NotNull(result.Model.Frames);
        Assert.Equal("MassSources", Assert.Single(result.Validation.Issues).Component);
    }
    [Fact]
    public void TakeoffsAreAttachedAndReadTwiceForConsistency()
    {
        using var session=new FakeSession();var input=Extractor.Extract(session);
        Assert.NotNull(input.MaterialTakeoffs);Assert.Same(input.Validation.MaterialTakeoffs,input.MaterialTakeoffs);
        Assert.Equal(1000,Assert.Single(input.MaterialTakeoffs.Items).TotalWeight);
        Assert.Equal(2,session.TakeoffReads);
    }
    [Fact]
    public void MissingTakeoffDoesNotDiscardUnrelatedData()
    {
        using var session=new FakeSession {FailTakeoff=true};var input=Extractor.Extract(session);
        Assert.Null(input.MaterialTakeoffs);Assert.NotNull(input.Validation.LoadPatterns);
        Assert.Equal("MaterialTakeoffs",Assert.Single(input.Validation.Issues).Component);
    }
    [Fact]
    public void ChangedTakeoffInvalidatesTheInspectionSnapshot()
    {
        using var session=new FakeSession {ChangeTakeoff=true};
        Assert.Throws<ModelChangedException>(()=>Extractor.Extract(session));
    }
    [Fact]
    public void ValidationChangesUnderTheSameModelIdentityInvalidateTheCapture()
    {
        using var session = new FakeSession { ChangePatterns = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(session));
    }
    [Fact]
    public void ValidationCancellationDoesNotScheduleMoreReads()
    {
        using var session = new FakeSession();
        Assert.Throws<OperationCanceledException>(() => Extractor.Extract(session, new CancellationToken(true)));
        Assert.Equal(0, session.PatternReads);
    }
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DesignExtractionIsolatesMemberFailures(bool failColumns, bool failBeams, bool failPiers)
    {
        using var session = new FakeSession { FailColumns = failColumns, FailBeams = failBeams, FailPiers = failPiers };
        var result = Extractor.Extract(session, readDesign: true);
        Assert.NotNull(result.ConcreteColumns); Assert.NotNull(result.ConcreteBeams);
        Assert.Equal(failColumns ? 0 : 1, result.ConcreteColumns.Items.Count);
        Assert.Equal(failBeams ? 0 : 1, result.ConcreteBeams.Items.Count);
        Assert.NotNull(result.ConcretePiers);
        Assert.Equal(failPiers ? 0 : 1, result.ConcretePiers.Items.Count);
        if (failPiers) Assert.Contains(result.ConcretePiers.Limitations, x => x.Contains("controlled"));
        else
        {
            var pier = Assert.Single(result.ConcretePiers.Items);
            Assert.Equal("P1", pier.PierName); Assert.Equal("L1", pier.StoryName);
            Assert.Equal(1500, pier.RequiredVerticalRebarArea);
        }
        if (failColumns) Assert.Contains(result.ConcreteColumns.Limitations, x => x.Contains("controlled"));
        if (failBeams) Assert.Contains(result.ConcreteBeams.Limitations, x => x.Contains("controlled"));
        if (!failBeams)
        {
            var beam = Assert.Single(result.ConcreteBeams.Items);
            Assert.Equal("TopU", beam.FlexuralCombo); Assert.Equal("BottomU", beam.BottomFlexuralCombo);
            Assert.Equal(50, beam.TorsionRebarArea); Assert.Equal(.2, beam.TransverseTorsionRebarPerLength);
        }
    }
    [Fact]
    public void DesignReadsAreOptIn()
    {
        using var session = new FakeSession();
        var result = Extractor.Extract(session);
        Assert.Null(result.ConcreteBeams); Assert.Null(result.ConcreteColumns); Assert.Null(result.ConcretePiers);
        Assert.Equal(0, session.DesignReads);
    }
    [Fact]
    public void ProvidedScheduleIsAttachedToTheInspectionInput()
    {
        using var session=new FakeSession();
        var configuration=Context().Configuration with {ProvidedRebar=[new("C1",2000)]};
        var result=Extractor.Extract(session,readDesign:true,configuration:configuration);
        Assert.Equal(configuration.ProvidedRebar,result.ProvidedRebar);
        Assert.Equal("C1",Assert.Single(result.ProvidedRebar!).ElementId);
    }
    [Fact]
    public void ConfirmedUnrunDesignDoesNotRequestMemberSummaries()
    {
        using var session = new FakeSession { DesignAvailable = false };
        var result = Extractor.Extract(session, readDesign: true);
        Assert.Empty(result.ConcreteColumns!.Items);
        Assert.Empty(result.ConcreteBeams!.Items);
        Assert.Equal(1, session.DesignReads); // Wall design is an independent operation.
        Assert.Equal(OperationState.NotRun, ModelStateAssessment.Assess(result).ConcreteDesign);
    }
    [Fact]
    public void UnrunAnalysisDoesNotRequestAnalysisResults()
    {
        using var session = new FakeSession { AnalysisState = AnalysisCaseState.NotRun };
        var result = Extractor.Extract(session, readAnalysis: true);
        Assert.Equal(0, session.ResultReads);
        Assert.NotNull(result.Analysis!.Definitions);
        Assert.Equal(OperationState.NotRun, ModelStateAssessment.Assess(result).Analysis);
    }
    [Fact]
    public void AnalysisAndDesignChangesDuringReadDiscardTheSnapshot()
    {
        using var designSession = new FakeSession { DesignAvailable = true, ChangeDesignState = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(designSession, readDesign: true));
        using var analysisSession = new FakeSession { ChangeAnalysisState = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(analysisSession, readAnalysis: true));
    }
    [Fact]
    public void FailedDesignStatusReadPreservesIndependentDesignAndGeometryData()
    {
        using var session = new FakeSession { FailDesignState = true };
        var result = Extractor.Extract(session, readDesign: true);
        Assert.NotEmpty(result.ConcreteDesignStatus!.Issues);
        Assert.NotEmpty(result.ConcreteBeams!.Items);
        Assert.NotNull(result.Model.Frames);
        Assert.NotEqual(OperationState.NotRun, ModelStateAssessment.Assess(result).ConcreteDesign);
    }
    private sealed class FakeSession : IValidationSession, IConcreteDesignSession, IConcreteDesignStateSession, IAnalysisSession
    {
        private readonly InspectionInput input = Context().Input;
        public bool? DesignAvailable { get; init; }
        public bool ChangeDesignState { get; init; }
        public bool FailDesignState { get; init; }
        private int stateReads;
        public ConcreteDesignStatusData ReadConcreteDesignStatus()
        {
            stateReads++;
            if (FailDesignState) throw new ApiException("GetResultsAvailable", 1, "controlled state failure");
            return new(ChangeDesignState && stateReads > 1 ? false : DesignAvailable,
                Data(new ConcreteDesignMember("F1", "Beam", 2)), []);
        }
        public AnalysisCaseState AnalysisState { get; init; } = AnalysisCaseState.Finished;
        public bool ChangeAnalysisState { get; init; }
        private int analysisReads;
        public int ResultReads { get; private set; }
        public void RunAnalysis() => throw new InvalidOperationException("Inspection attempted a model mutation.");
        public AnalysisStatusData ReadAnalysisStatus()
        {
            analysisReads++;
            var state = ChangeAnalysisState && analysisReads > 1 ? AnalysisCaseState.NotRun : AnalysisState;
            return new(Data(input.Validation.LoadCases!.Items.Select(c => new AnalysisCaseStatus(c.Name, state, (int)state)).ToArray()));
        }
        public AnalysisResultsData ReadSelectedAnalysisResults()
        {
            ResultReads++;
            return new(Data<StoryDriftResult>(), Data<ModalParticipationResult>(), Data<BaseReactionResult>(), [], [], [], []);
        }
        public AnalysisDefinitionData ReadAnalysisDefinitions() => new(Data<ResponseSpectrumCase>(), Data<GeometricNonlinearitySetting>());
        public bool FailMass { get; init; }
        public bool FailTakeoff { get; init; }
        public bool ChangeTakeoff { get; init; }
        public int TakeoffReads { get; private set; }
        public DataSet<MaterialTakeoffResult> ReadMaterialTakeoff()
        {
            TakeoffReads++;
            if (FailTakeoff) throw new ApiException("Material takeoff table", 1, "controlled takeoff failure");
            return Data(new MaterialTakeoffResult("Column", "C25", ChangeTakeoff && TakeoffReads > 1 ? 2000 : 1000, 40));
        }
        public bool ChangePatterns { get; init; }
        public int PatternReads { get; private set; }
        public bool FailColumns { get; init; }
        public bool FailBeams { get; init; }
        public bool FailPiers { get; init; }
        public int DesignReads { get; private set; }
        public DataSet<ConcretePierDesignResult> ReadConcretePierDesign()
        {
            DesignReads++;
            if (FailPiers) throw new ApiException("Pier summary", 1, "controlled");
            return Data(new ConcretePierDesignResult("P1", "L1", null, null, null, 1500, .75, "", "")
                { Station = "Top", PierLeg = "Top Leg 1", DesignType = "Uniform" });
        }
        public DataSet<ConcreteColumnDesignResult> ReadConcreteColumnDesign()
        {
            DesignReads++;
            if (FailColumns) throw new ApiException("Column summary", 1, "controlled");
            return Data(new ConcreteColumnDesignResult("C1", "B", "U", .5, null, null, null));
        }
        public DataSet<ConcreteBeamDesignResult> ReadConcreteBeamDesign()
        {
            DesignReads++;
            if (FailBeams) throw new ApiException("Beam summary", 1, "controlled");
            return Data(new ConcreteBeamDesignResult("F1", "B", 0, 1000, 800, .5, 50, "TopU", "ShearU", "", "")
                { BottomFlexuralCombo = "BottomU", TransverseTorsionRebarPerLength = .2 });
        }
        public ModelIdentity ReadIdentity() => input.Model.Identity;
        public StoryData ReadStories() => input.Model.Stories!;
        public DataSet<Joint> ReadJoints() => input.Model.Joints!;
        public DataSet<Frame> ReadFrames() => input.Model.Frames!;
        public DataSet<Area> ReadAreas() => input.Model.Areas!;
        public DataSet<Material> ReadMaterials() => input.Model.Materials!;
        public DataSet<FrameSection> ReadFrameSections() => input.Model.FrameSections!;
        public DataSet<AreaSection> ReadAreaSections() => input.Model.AreaSections!;
        public DataSet<JointAssignment> ReadJointAssignments() => input.Validation.JointAssignments!;
        public DataSet<FrameAssignment> ReadFrameAssignments() => input.Validation.FrameAssignments!;
        public DataSet<AreaAssignment> ReadAreaAssignments() => input.Validation.AreaAssignments!;
        public DataSet<LinkConnection> ReadLinks() => input.Validation.Links!;
        public DataSet<LoadPattern> ReadLoadPatterns()
        {
            PatternReads++;
            return ChangePatterns && PatternReads > 1 ? Data(new LoadPattern("different", "Other", 0)) : input.Validation.LoadPatterns!;
        }
        public DataSet<LoadCase> ReadLoadCases() => input.Validation.LoadCases!;
        public DataSet<LoadCombination> ReadCombinations() => input.Validation.Combinations!;
        public DataSet<MassSource> ReadMassSources() => FailMass ? throw new ApiException("Mass table", 3, "controlled") : input.Validation.MassSources!;
        public DataSet<Diaphragm> ReadDiaphragms() => input.Validation.Diaphragms!;
        public void Dispose() { }
    }
}


