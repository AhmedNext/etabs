// Areas use reinforcementAreaUnit when configured, otherwise the live ETABS display length squared.
public sealed record ProvidedRebar(
    [property: System.Text.Json.Serialization.JsonRequired] string ElementId,
    [property: System.Text.Json.Serialization.JsonRequired] double LongitudinalAreaProvided)
{
    public bool IsGeneratedBaseline { get; init; }
    public int? BarCount { get; init; }
    public double? BarDiameterMm { get; init; }
}
