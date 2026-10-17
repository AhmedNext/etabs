public sealed class RebarEstimationTests
{
    private static InspectionInput Input(double volume=468636000000, string unit="mm")
    {
        var c=Context().Model(m=>m with {Identity=m.Identity with {DisplayUnits=new("N",unit,"C")}});
        return c.Input with {MaterialTakeoffs=Data(new MaterialTakeoffResult("Beam","C25",0,volume))};
    }
    [Fact]
    public void DefaultScenariosProduceExpectedSteelMassFromCubicMillimetres()
    {
        var input=Input();var estimate=RebarEstimator.Estimate(input,new());
        Assert.Equal(468.636,estimate.TotalConcreteM3!.Value,8);
        Assert.Equal(56236.32,estimate.LowSteelKg!.Value,6);
        Assert.Equal(63265.86,estimate.BaseSteelKg!.Value,6);
        Assert.Equal(70295.4,estimate.HighSteelKg!.Value,6);
        Assert.Null(input.ProvidedRebar);
    }
    [Fact]
    public void MetreAndMillimetreVolumesProduceEquivalentEstimates()
    {
        var metres=RebarEstimator.Estimate(Input(468.636,"m"),new());
        var millimetres=RebarEstimator.Estimate(Input(),new());
        Assert.Equal(metres.BaseSteelKg!.Value,millimetres.BaseSteelKg!.Value,6);
    }
    [Fact]
    public void ElementRateOverridesAreUsedAndStructuralSteelIsNotCountedAsConcrete()
    {
        var input=Input(1,"m");
        input=input with {Model=input.Model with {Materials=Data(input.Model.Materials!.Items[0],input.Model.Materials.Items[0] with {Name="Steel",Type="Steel"})},
            MaterialTakeoffs=Data(new MaterialTakeoffResult("Beam","C25",0,1),new("Column","C25",0,2),new("Frame","Steel",0,100))};
        var settings=new RebarEstimationSettings {ByObjectType=new Dictionary<string,RebarEstimationRates>{{"Beam",new(150,160,170)}}};
        var estimate=RebarEstimator.Estimate(input,settings);
        Assert.Equal(2,estimate.Items.Count);Assert.Equal(430,estimate.BaseSteelKg);
    }
    [Fact]
    public void MissingAndInvalidQuantitiesAreDisclosedRatherThanReportedAsZeroSteel()
    {
        Assert.Null(RebarEstimator.Estimate(Context().Input,new()).BaseSteelKg);
        var invalid=RebarEstimator.Estimate(Input(double.NaN),new());
        Assert.Empty(invalid.Items);Assert.Null(invalid.BaseSteelKg);Assert.Contains(invalid.Limitations,l=>l.Contains("excluded"));
        var input=Input();input=input with {Model=input.Model with {Materials=null}};
        Assert.Null(RebarEstimator.Estimate(input,new()).BaseSteelKg);
    }
    [Fact]
    public void InvalidConfiguredRatesAreRejected()
    {
        foreach(var rates in new[]{new RebarEstimationRates(150,135,120),new(0,135,150),new(120,double.NaN,150)})
            Assert.Throws<ConfigurationException>(()=>ProjectConfiguration.Validate(new() {RebarEstimation=new() {DefaultRates=rates}}));
    }
    [Fact]
    public void HtmlIncludesTheEstimateAndAllThreeScenarios()
    {
        var input=Input();input=input with {RebarEstimate=RebarEstimator.Estimate(input,new())};
        var report=new InspectionReport("test","all",input,Context().Configuration,[],[],DateTimeOffset.UnixEpoch);
        var file=Path.Combine(AppContext.BaseDirectory,"rebar-estimate-"+Guid.NewGuid().ToString("N")+".html");
        try
        {
            HtmlReportGenerator.GenerateReport(report,file);var html=File.ReadAllText(file);
            Assert.Contains("Material quantities &amp; rebar estimation",html);Assert.Contains("468.636",html);
            Assert.Contains("56.236",html);Assert.Contains("63.266",html);Assert.Contains("70.295",html);
        }
        finally{if(File.Exists(file))File.Delete(file);}
    }
}
