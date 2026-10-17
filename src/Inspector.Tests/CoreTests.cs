
public sealed class CoreTests
{
    [Fact]
    public void MissingRunningInstanceIsAConnectionFailure() =>
        Assert.Throws<ConnectionException>(() => ConnectionGuard.RequireActiveInstance<object>(null));

    [Fact]
    public void ExistingInstanceIsReturnedWithoutLaunchingAnything()
    {
        var instance = new object();
        Assert.Same(instance, ConnectionGuard.RequireActiveInstance(instance));
    }

    [Fact]
    public void MissingRequiredTableFieldIsAnApiFailure() => Assert.Throws<ApiException>(() =>
        DatabaseTable.Parse("summary", 1, ["Name"], 1, ["Slab1"]).RequireFields("Name", "Type"));

    [Fact]
    public void CapabilityIdsAreUniqueAndUntestedBranchesStayExplicit()
    {
        Assert.Equal(CapabilityRegistry.All.Count, CapabilityRegistry.All.Select(c => c.Id).Distinct().Count());
        Assert.Equal(CapabilityState.UNTESTED, Assert.Single(CapabilityRegistry.All, c => c.Id == "API-CIRCLE").State);
    }
    [Theory]
    [InlineData("22.7.0")]
    [InlineData("22.7.0.0")]
    public void SupportedVersion(string version) => VersionPolicy.Validate(version);

    [Theory]
    [InlineData("22.5.0")]
    [InlineData("22.7.1")]
    [InlineData("22.70.0")]
    [InlineData("23.7.0")]
    [InlineData("22.7")]
    [InlineData("")]
    [InlineData("unknown")]
    public void UnsupportedVersionIsNeverAccepted(string version) => Assert.Throws<ConnectionException>(() => VersionPolicy.Validate(version));

    [Fact]
    public void ApiReturnCodeIsNotSwallowed()
    {
        var api = new ApiGuard(NullLogger.Instance, default);
        var error = Assert.Throws<ApiException>(() => api.Call("FrameObj.GetAllFrames", () => 7));
        Assert.Equal(7, error.ReturnCode);
        Assert.Equal("FrameObj.GetAllFrames", error.Operation);
    }
    [Fact]
    public void MalformedParallelArraysAreRejected() =>
        Assert.Throws<ApiException>(() => ApiGuard.Arrays("Points", 2, new[] { "a", "b" }, new[] { 1d }));
    [Fact]
    public void EmptyArrayIsDifferentFromApiFailure() => ApiGuard.Arrays("Points", 0, Array.Empty<string>(), Array.Empty<double>());
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCoordinatesAreRejected(double value) => Assert.Throws<ApiException>(() => ApiGuard.Finite("Coordinates", value));
    [Fact]
    public void ZeroDimensionIsPreservedForLaterGeometryChecks() => Assert.Equal(0, ApiGuard.Finite("Section", 0));
    [Fact]
    public void CancellationStopsBeforeNextApiCall()
    {
        bool called = false;
        var api = new ApiGuard(NullLogger.Instance, new CancellationToken(true));
        Assert.Throws<OperationCanceledException>(() => api.Call("Any", () => { called = true; return 0; }));
        Assert.False(called);
    }
    [Fact]
    public async Task CrossThreadComUseIsRejected()
    {
        var guard = new ApiGuard(NullLogger.Instance, default);
        await StaRunner.Run(() => { Assert.Throws<InvalidOperationException>(() => guard.CheckThread()); return true; });
    }
    [Fact]
    public async Task ComExecutorUsesStaAndPropagatesErrors()
    {
        Assert.Equal(ApartmentState.STA, await StaRunner.Run(() => Thread.CurrentThread.GetApartmentState()));
        await Assert.ThrowsAsync<ApiException>(() => StaRunner.Run<int>(() => throw new ApiException("read", 4, "error")));
    }
    [Fact]
    public void TablePreservesEmptyCellsAndRowBoundaries()
    {
        var table = DatabaseTable.Parse("test", 1, ["Name", "Material"], 2, ["one", "", "two", "C30"]);
        Assert.Equal("", table.Rows[0]["Material"]);
        Assert.Equal("two", table.Rows[1]["Name"]);
        Assert.Equal("C30", table.Rows[1]["Material"]);
    }
    [Fact]
    public void TruncatedTableIsRejected() =>
        Assert.Throws<ApiException>(() => DatabaseTable.Parse("test", 1, ["a", "b"], 2, ["1", "2", "3"]));
    [Fact]
    public void DuplicateTableFieldsAreRejected() =>
        Assert.Throws<ApiException>(() => DatabaseTable.Parse("test", 1, ["a", "a"], 1, ["1", "2"]));
    [Fact]
    public void UnitsAreExplicitAndDimensionallyConsistent()
    {
        var units = new UnitContext("N", "mm", "C");
        Assert.Equal("N/mm²", units.Stress);
        Assert.Equal("N/mm³", units.UnitWeight);
        Assert.Equal("N·s²/mm⁴", units.MassDensity);
    }
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--evidence")]
    public void InvalidOptionsFailBeforeConnecting(string option) =>
        Assert.Throws<ConfigurationException>(() => CliOptions.Parse(["extract", option]));
    [Fact]
    public void DesignCommandIsAvailable() => Assert.Equal("design", CliOptions.Parse(["inspect", "design"]).Module);
    [Fact]
    public void EvidenceCannotUseAnEdbFile() => Assert.Throws<ConfigurationException>(() => CliOptions.Parse(["extract", "--evidence", "model.edb"]));
    [Fact]
    public void HelpNeedsNoLiveEtabs() => Assert.Equal("help", CliOptions.Parse([]).Command);
    [Fact]
    public void ExitCodesAreDistinct() => Assert.Equal(7, Enum.GetValues<ExitCode>().Select(x => (int)x).Distinct().Count());
}


