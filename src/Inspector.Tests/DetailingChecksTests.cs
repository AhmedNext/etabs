public sealed class DetailingChecksTests
{
    private static InspectionContext Fixture(double provided=1500, double? required=1500)
    {
        var c=Context();
        return c with {Input=c.Input with {ConcreteColumns=Data(new ConcreteColumnDesignResult("F1","B","U",null,null,null,required)),
            ProvidedRebar=[new("F1",provided)]}};
    }
    private static Finding Run(InspectionContext c)=>Assert.Single(DetailingChecks.Create().Single().Evaluate(c));
    [Theory]
    [InlineData(1499,CheckStatus.FAIL)]
    [InlineData(1500,CheckStatus.PASS)]
    [InlineData(1600,CheckStatus.PASS)]
    public void ProvidedAreaMustMeetEveryRequiredArea(double provided,CheckStatus expected)
    {
        var result=Run(Fixture(provided));Assert.Equal(expected,result.Status);
        Assert.Equal(provided,result.Actual);Assert.Equal(1500,result.Limit);
        Assert.Equal("Frame",result.ElementType);Assert.Equal("F1",result.ElementId);
    }
    [Theory]
    [InlineData("cm2",15)]
    [InlineData("m2",.0015)]
    [InlineData("mm²",1500)]
    public void AreaUnitsAreConvertedToModelDisplayUnits(string unit,double provided)
    {
        var c=Fixture(provided).Settings(s=>s with {ReinforcementAreaUnit=unit});
        var result=Run(c);Assert.Equal(CheckStatus.PASS,result.Status);Assert.Equal(1500,result.Actual!.Value,8);
    }
    [Fact]
    public void MissingAmbiguousAndInvalidProvidedAreasNeverPass()
    {
        var c=Fixture();
        foreach(var entries in new IReadOnlyList<ProvidedRebar>[]{[new("other",2000)],[new("F1",1500),new("F1",2000)], [new("F1",double.NaN)],[new("F1",-1)]})
            Assert.Equal(CheckStatus.NOT_VERIFIED,Run(c with {Input=c.Input with {ProvidedRebar=entries}}).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run(Fixture(required:null)).Status);
        Assert.Equal(CheckStatus.NOT_APPLICABLE,Run(Context()).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run(c with {Input=c.Input with {ConcreteColumns=Data(c.Input.ConcreteColumns!.Items[0] with {ErrorSummary="design failed"})}}).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptyScheduleProducesOneProjectLevelNote(bool empty)
    {
        var c=Fixture();
        var row=c.Input.ConcreteColumns!.Items[0];
        c=c with {Input=c.Input with {ConcreteColumns=Data(row,row with {FrameId="F2"},row with {Location=3000}),
            ProvidedRebar=empty?[]:null}};
        var finding=Run(c);
        Assert.Equal(CheckStatus.NOT_APPLICABLE,finding.Status);
        Assert.Equal("model",finding.ElementId);
        Assert.Contains("External provided reinforcement schedule was not supplied; rebar detailing comparison is skipped.",finding.Title);
    }
    [Theory]
    [InlineData("all")]
    [InlineData("design")]
    public void ReconciliationIsRegisteredForBothInspectionCommands(string module)
    {
        var c=Fixture();var result=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,module,CheckCatalog.Implemented,CheckCatalog.All);
        Assert.Equal(CheckStatus.PASS,Assert.Single(result.Findings,f=>f.CheckId=="DETAIL-008").Status);
    }
    private static void WithSchedule(string? json,Action<string> test)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"rebar-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config=Path.Combine(directory,"project.json");
            File.WriteAllText(config,"""{"schemaVersion":"1.0","confirmed":true,"reinforcementFile":"reinforcement.json","reinforcementAreaUnit":"mm2"}""");
            if(json is not null)File.WriteAllText(Path.Combine(directory,"reinforcement.json"),json);
            test(config);
        }
        finally{foreach(var file in Directory.EnumerateFiles(directory))File.Delete(file);Directory.Delete(directory);}
    }
    [Fact]
    public void ScheduleLoadsRelativeToConfigurationWithSourceHash()
    {
        WithSchedule("""[{"elementId":"F1","longitudinalAreaProvided":1500}]""",path=>{
            var config=ProjectConfiguration.Load(path,true);var row=Assert.Single(config.ProvidedRebar!);
            Assert.Equal("F1",row.ElementId);Assert.Equal(1500,row.LongitudinalAreaProvided);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(path)!,"reinforcement.json"),config.ReinforcementSourcePath);
            Assert.Equal(64,config.ReinforcementSha256!.Length);
        });
    }
    [Fact]
    public void OptionalMissingScheduleIsAnExplicitLimitation()
    {
        WithSchedule(null,path=>{var config=ProjectConfiguration.Load(path,true);Assert.Null(config.ProvidedRebar);Assert.NotEmpty(config.ReinforcementLimitations);});
    }
    [Theory]
    [InlineData("[{\"elementId\":\"F1\"}]")]
    [InlineData("[{\"longitudinalAreaProvided\":1500}]")]
    [InlineData("[{\"elementId\":\"F1\",\"longitudinalAreaProvided\":-1}]")]
    [InlineData("[{\"elementId\":\"F1\",\"longitudinalAreaProvided\":1500},{\"elementId\":\"F1\",\"longitudinalAreaProvided\":2000}]")]
    [InlineData("[{\"elementId\":\"F1\",\"elementId\":\"F2\",\"longitudinalAreaProvided\":1500}]")]
    [InlineData("[null]")]
    public void InvalidSchedulesAreRejectedBeforeEngineeringChecks(string json)=>
        WithSchedule(json,path=>Assert.Throws<ConfigurationException>(()=>ProjectConfiguration.Load(path,true)));
}
