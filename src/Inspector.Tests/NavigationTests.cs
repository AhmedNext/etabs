public sealed class NavigationTests
{
    [Fact]
    public void LocateFailedAcceptsConfiguration()
    {
        var options = CliOptions.Parse(["locate", "failed", "--config", "project.json"]);
        Assert.Equal("locate", options.Command); Assert.Equal("all", options.Module);
        Assert.True(options.ExplicitConfiguration); Assert.Equal("project.json", options.ConfigurationPath);
    }
    [Theory]
    [InlineData("locate")]
    [InlineData("locate", "story")]
    [InlineData("locate", "story", "--config")]
    [InlineData("locate", "--config")]
    [InlineData("locate", "failed", "--details")]
    [InlineData("locate", "failed", "--evidence", "x.json")]
    public void InvalidNavigationArgumentsFailBeforeConnecting(params string[] args) =>
        Assert.Throws<ConfigurationException>(() => CliOptions.Parse(args));

    [Theory]
    [InlineData("C17")]
    [InlineData("Frame:C17")]
    [InlineData("Area:A1")]
    public void SpecificElementTargetIsPreserved(string target)
    {
        var options = CliOptions.Parse(["locate", target]);
        Assert.Equal(target, options.LocateTarget); Assert.False(options.LocateStory);
    }
    [Fact]
    public void StoryTargetIsDistinctFromReservedFailedTarget()
    {
        var options = CliOptions.Parse(["locate", "story", "failed"]);
        Assert.Equal("failed", options.LocateTarget); Assert.True(options.LocateStory);
        Assert.False(CliOptions.Parse(["locate", "failed"]).LocateStory);
    }
    [Fact]
    public void StorySelectionIncludesOnlyMatchingTypedObjects()
    {
        var model = Context().Input.Model;
        model = model with { Frames = Data(model.Frames!.Items[0], model.Frames.Items[0] with { Id = "upper", Story = "L2" }),
            Areas = Data(model.Areas!.Items[0] with { Id = "F1" }, model.Areas.Items[0] with { Id = "upper", Story = "L2" }) };
        Assert.Equal(new[] { "Area:F1", "Frame:F1" }, Program.StoryElementIds(model, "L1"));
        Assert.Throws<ConfigurationException>(() => Program.StoryElementIds(model, "missing"));
        Assert.Throws<ApiException>(() => Program.StoryElementIds(model with { Areas = null }, "L1"));
    }

    [Fact]
    public void FailuresAreDeduplicatedAndTypedBeforeSelection()
    {
        var c = Context().Model(m => m with { Areas = Data(m.Areas!.Items[0], m.Areas.Items[0] with { Id = "F1" }) });
        var check = Rule.Create("GEO-999", "Navigation fixture", "Synthetic navigation inputs", [], _ => [
            new(CheckStatus.FAIL, "frame", "F1", "Frame"),
            new(CheckStatus.FAIL, "duplicate", "Frame:F1", "Frame"),
            new(CheckStatus.FAIL, "area sharing frame ID", "F1", "Area"),
            new(CheckStatus.FAIL, "prefixed area", "Area:S1"),
            new(CheckStatus.WARNING, "warning must not be selected", "S1", "Area"),
            new(CheckStatus.FAIL, "ambiguous untyped ID", "F1"),
            new(CheckStatus.FAIL, "section failure", "B", "FrameSection"),
            new(CheckStatus.FAIL, "unknown object", "missing", "Frame"),
            new(CheckStatus.FAIL, "model-level failure", "model"),
            new(CheckStatus.FAIL, "no object")
        ]);
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [check], [check.Definition]);
        Assert.Equal(new[] { "Area:F1", "Area:S1", "Frame:F1" }, Program.FailedElementIds(report));
    }
    [Fact]
    public void NoFailuresProducesEmptySelection()
    {
        var c = Context();
        var check = Rule.Create("GEO-999", "Navigation fixture", "No failure", [], _ => [new(CheckStatus.WARNING, "review", "F1", "Frame")]);
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [check], [check.Definition]);
        Assert.Empty(Program.FailedElementIds(report));
    }
}
