/// <summary>Geometry inputs from tools/ETABS2016-Software-Verification.pdf, Example 1, printed page 1-1 (PDF page 21).
/// These validate the inspector's area calculation; they do not rerun or certify the CSI solver.</summary>
public sealed class CsiVerificationReferenceTests
{
    [Theory]
    [InlineData(12, 24, 288)]
    [InlineData(12, 30, 360)]
    public void PublishedExampleOneRectangleDimensionsGiveCorrectGrossAreas(double widthInches, double depthInches, double areaInchesSquared)
    {
        Assert.Equal(areaInchesSquared, SectionGeometry.GrossArea(new("Example 1", "Rectangular", null, depthInches, widthInches, null, null)), 10);
        var inchesToMeters = GeometryMath.MetersPerUnit("in");
        var squareMeters = SectionGeometry.GrossArea(new("Example 1", "Rectangular", null,
            depthInches * inchesToMeters, widthInches * inchesToMeters, null, null));
        Assert.Equal(areaInchesSquared * .00064516, squareMeters, 10);
    }
}
