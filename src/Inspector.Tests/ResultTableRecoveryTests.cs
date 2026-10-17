public sealed class ResultTableRecoveryTests
{
    private static AnalysisEvidenceTable BaseTable(string value="11117.664")=>new("Base Reactions",
        ["OutputCase","CaseType","FX","FY","FZ","MX","MY","MZ","X","Y","Z"],
        [new Dictionary<string,string>{{"OutputCase","Dead"},{"CaseType","LinStatic"},{"FX","0"},{"FY","0"},{"FZ",value},{"MX","122294.2775"},{"MY","-55588.4586"},{"MZ","0.0009"},{"X","0"},{"Y","0"},{"Z","0"}}]);
    [Fact]
    public void ConvertsScreenUnitsAndPreservesTheSourceRoundingBound()
    {
        var table=ResultTableRecovery.Normalize(BaseTable(),new("kN","m","C"),new("N","mm","C"),"screen",DateTimeOffset.UtcNow);
        var row=Assert.Single(table.Rows);
        Assert.Equal(11117664,ResultTableRecovery.Parse(row["FZ"]),6);
        Assert.Equal(.5,ResultTableRecovery.Parse(row["__uncertainty_FZ"]),10);
        Assert.Equal(122294277500,ResultTableRecovery.Parse(row["MX"]),3);
        Assert.Equal("screen",table.RecoverySource);
        var imperial=ResultTableRecovery.Normalize(BaseTable(),new("kN","m","C"),new("kip","inch","F"),"screen",DateTimeOffset.UtcNow);
        Assert.InRange(ResultTableRecovery.Parse(imperial.Rows[0]["FZ"]),2499,2500);
    }
    [Theory]
    [InlineData("NaN")][InlineData("Infinity")][InlineData("invalid")]
    public void InvalidNumericCellsAreRejected(string text)=>Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Normalize(BaseTable(text),new("N","mm","C"),new("N","mm","C"),"screen",DateTimeOffset.UtcNow));
    [Fact]
    public void UnknownUnitsEmptyTablesAndDefinitionTablesAreRejected()
    {
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Normalize(BaseTable(),new("unknown","m","C"),new("N","mm","C"),"screen",DateTimeOffset.UtcNow));
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Normalize(BaseTable() with{Rows=[]},new("N","mm","C"),new("N","mm","C"),"screen",DateTimeOffset.UtcNow));
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Normalize(BaseTable() with{Key="Frame Assignments"},new("N","mm","C"),new("N","mm","C"),"screen",DateTimeOffset.UtcNow));
    }
    [Fact]
    public void ModelBindingRejectsChangedFilesUnlockedModelsAndIncompleteOrStaleBundles()
    {
        var path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"synthetic model identity fixture");File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddMinutes(-2));
            var model=new ModelIdentity("22.7.0",path,true,new("N","mm","C"),new("N","mm","C"),DateTimeOffset.UtcNow);
            var bundle=new ResultTableBundle(path,ResultTableRecovery.HashModel(path),DateTimeOffset.UtcNow,new("kN","m","C"),"test export",true,[BaseTable()]);
            Assert.Single(ResultTableRecovery.Validate(bundle,model));
            Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Validate(bundle,model with{IsLocked=false}));
            Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Validate(bundle with{Complete=false},model));
            Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Validate(bundle with{CapturedAt=DateTimeOffset.UtcNow.AddDays(-1)},model));
            Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Validate(bundle with{Tables=[BaseTable(),BaseTable()]},model));
            File.AppendAllText(path,"changed");
            Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.Validate(bundle,model));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void CsvPreservesQuotedValuesAndRejectsTruncationOrMultipleTables()
    {
        var table=ResultTableRecovery.ReadCsv("Base Reactions","TABLE: Base Reactions\r\nOutput Case,Case Type,FZ\r\n\"Dead, roof\",LinStatic,11117.664\r\n");
        Assert.Equal("Dead, roof",table.Rows[0]["OutputCase"]);Assert.Equal("11117.664",table.Rows[0]["FZ"]);
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.ReadCsv("Base Reactions","Output Case,FZ\nDead"));
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.ReadCsv("Base Reactions","Output Case,FZ\n\"Dead,1"));
        Assert.Throws<ConfigurationException>(()=>ResultTableRecovery.ReadCsv("Base Reactions","TABLE: Joint Reactions\nFZ\n1"));
    }
    [Fact]
    public void CommandLineAcceptsAnExplicitBundleOnlyForInspectionOrReports()
    {
        Assert.Equal("tables.json",CliOptions.Parse(["report","--result-tables","tables.json"]).ResultTablesPath);
        Assert.Equal("tables.json",CliOptions.Parse(["inspect","analysis","--result-tables","tables.json"]).ResultTablesPath);
        Assert.Throws<ConfigurationException>(()=>CliOptions.Parse(["connect","--result-tables","tables.json"]));
        Assert.Throws<ConfigurationException>(()=>CliOptions.Parse(["report","--result-tables"]));
    }
}
