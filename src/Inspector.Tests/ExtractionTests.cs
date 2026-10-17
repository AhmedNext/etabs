
public sealed class ExtractionTests
{
    private static readonly UnitContext Units = new("kN", "m", "C");
    private static ModelIdentity Identity => new("22.7.0", "C:\\test.edb", true, Units, Units, DateTimeOffset.UtcNow);
    private static ModelExtractor Extractor => new(NullLogger<ModelExtractor>.Instance);

    [Fact]
    public void IndependentDataStillExtractsAfterApiFailure()
    {
        using var source = new FakeSession { FailFrames = true };
        var data = Extractor.Extract(source);
        Assert.Null(data.Frames);
        Assert.NotNull(data.Materials);
        Assert.NotNull(data.AreaSections);
        Assert.False(data.IsComplete);
        Assert.Equal("Frames", Assert.Single(data.Issues).Component);
        Assert.Equal(11, data.Issues[0].ReturnCode);
    }
    [Fact]
    public void ContextSwitchDiscardsExtraction()
    {
        using var source = new FakeSession { ChangeModel = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(source));
    }
    [Fact]
    public void UnitSwitchDiscardsExtraction()
    {
        using var source = new FakeSession { ChangeUnits = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(source));
    }
    [Fact]
    public void CancellationDoesNotReadGeometry()
    {
        using var source = new FakeSession();
        Assert.Throws<OperationCanceledException>(() => Extractor.Extract(source, new CancellationToken(true)));
        Assert.Equal(0, source.GeometryReads);
    }
    [Fact]
    public void MissingDimensionsMakeExtractionExplicitlyPartial()
    {
        using var source = new FakeSession { UnsupportedSection = true };
        var data = Extractor.Extract(source);
        Assert.False(data.IsComplete);
        Assert.Single(data.FrameSections!.Limitations);
        Assert.Empty(data.Issues);
    }
    [Fact]
    public void EmptyModelIsNotAnApiFailure()
    {
        using var source = new FakeSession();
        var data = Extractor.Extract(source);
        Assert.Empty(data.Joints!.Items);
        Assert.True(data.IsComplete);
    }
    [Fact]
    public void ContextDoesNotUseReadTimestampAsIdentity() => Assert.True(ModelConsistency.SameContext(Identity, Identity));

    [Fact]
    public void ChangedContentsWithTheSameIdentityAreRejected()
    {
        using var source = new FakeSession { ChangeContents = true };
        Assert.Throws<ModelChangedException>(() => Extractor.Extract(source));
    }

    private sealed class FakeSession : IModelSession
    {
        public DataSet<MaterialTakeoffResult> ReadMaterialTakeoff() => Data<MaterialTakeoffResult>();
        public bool FailFrames { get; init; }
        public bool ChangeModel { get; init; }
        public bool ChangeUnits { get; init; }
        public bool UnsupportedSection { get; init; }
        public bool ChangeContents { get; init; }
        public int GeometryReads { get; private set; }
        private int identityReads;
        public ModelIdentity ReadIdentity()
        {
            identityReads++;
            if (identityReads > 2 && ChangeModel) return Identity with { FilePath = "C:\\different.edb" };
            if (identityReads > 2 && ChangeUnits) return Identity with { DisplayUnits = new("N", "mm", "C") };
            return Identity;
        }
        public StoryData ReadStories() { GeometryReads++; return new(0, []); }
        public DataSet<Joint> ReadJoints() => ChangeContents && GeometryReads > 1
            ? new([new Joint("new", new(1, 2, 3))], [], []) : new([], [], []);
        public DataSet<Frame> ReadFrames() => FailFrames ? throw new ApiException("GetFrames", 11, "failed") : new([], [], []);
        public DataSet<Area> ReadAreas() => new([], [], []);
        public DataSet<Material> ReadMaterials() => new([], [], []);
        public DataSet<FrameSection> ReadFrameSections() => new([], [], UnsupportedSection ? ["Nonprismatic section unsupported"] : []);
        public DataSet<AreaSection> ReadAreaSections() => new([], [], []);
        public void Dispose() { }
    }
}


