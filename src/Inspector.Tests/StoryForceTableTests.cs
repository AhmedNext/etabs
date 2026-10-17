
public sealed class StoryForceTableTests
{
    private static DatabaseTable Table(string p) => DatabaseTable.Parse("Story Forces", 1,
        ["Story", "OutputCase", "StepType", "StepNumber", "Location", "P", "VX", "VY"], 1,
        ["L1", "Dead", "", "", "Bottom", p, "2.3", "-4"]);
    [Fact]
    public void StaticBlankStepIsNormalized()
    {
        var row = Assert.Single(StoryForceTable.Parse(Table("100")));
        Assert.Equal("Single Value", row.StepType); Assert.Equal(0, row.StepNumber);
        Assert.Equal(100, row.P); Assert.Equal(2.3, row.VX); Assert.Equal(-4, row.VY);
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public void InvalidForceIsRejected(string value) => Assert.Throws<ApiException>(() => StoryForceTable.Parse(Table(value)));
}


