
public sealed class ModalParticipationTableTests
{
    private static DatabaseTable Table(string mode = "2", string ux = "0.25") => DatabaseTable.Parse(
        "Modal Participating Mass Ratios", 1,
        ["Case", "Mode", "Period", "UX", "UY", "UZ", "SumUX", "SumUY", "SumUZ", "RX", "RY", "RZ", "SumRX", "SumRY", "SumRZ"],
        1, ["Modal", mode, "1.5", ux, "0.5", "0", "0.75", "0.9", "0", "0", "0", "0.1", "0", "0", "0.2"]);

    [Fact]
    public void MapsModeAndRatiosWithoutInventingSteps()
    {
        var row = Assert.Single(ModalParticipationTable.Parse(Table()));
        Assert.Equal("Mode", row.StepType);
        Assert.Equal(2, row.StepNumber);
        Assert.Equal(1.5, row.Period);
        Assert.Equal(0.25, row.UX);
        Assert.Equal(0.75, row.SumUX);
        Assert.Equal(0.9, row.SumUY);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    [InlineData("bad")]
    public void RejectsInvalidNumericResults(string value) =>
        Assert.Throws<ApiException>(() => ModalParticipationTable.Parse(Table(ux: value)));

    [Fact]
    public void MissingFieldCannotBecomeSuccessfulFallback() =>
        Assert.Throws<ApiException>(() => ModalParticipationTable.Parse(DatabaseTable.Parse("Modal", 1, ["Case"], 1, ["Modal"])));
}


