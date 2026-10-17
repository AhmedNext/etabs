
public sealed class StoryForceLiveTests
{
    [LiveEtabsFact]
    public Task StoryForceSchemaAndBaseEquilibriumAreVerifiedReadOnly() => StaRunner.Run(() => {
        using var s = EtabsSession.Attach(NullLogger.Instance);
        var before = s.ReadIdentity();
        var results = s.ReadSelectedAnalysisResults();
        var rows = results.StoryForces!.Items;
        Assert.NotEmpty(rows);
        foreach (var name in new[] { "EQx", "EQy" })
        {
            var story = Assert.Single(rows, x => x.Story == "Story1" && x.Case == name && x.Location == "Bottom");
            var reaction = Assert.Single(results.BaseReactions.Items, x => x.Case == name);
            var actual = Math.Abs(name == "EQx" ? story.VX : story.VY);
            var expected = Math.Abs(name == "EQx" ? reaction.FX : reaction.FY);
            Assert.True(Math.Abs(actual - expected) <= 1, "Rounded story-bottom shear must agree with base reaction within 1 display force unit.");
        }
        var after = s.ReadIdentity();
        Assert.Equal(before.FilePath, after.FilePath); Assert.Equal(before.IsLocked, after.IsLocked);
        Assert.Equal(before.DisplayUnits, after.DisplayUnits);
        return true;
    });
}


