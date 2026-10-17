public sealed class HtmlReportTests
{
    private static InspectionReport Report()
    {
        var c=Context().Settings(s=>s with {Project=JsonSerializer.SerializeToElement(new {name="Project <script>alert(1)</script>"}),
            SeismicVerification=new("ASCE 7-16",4.5,1,.02,1,1,[new("X","EX"),new("Y","EY")],[]) });
        c=c.Validation(v=>v with {LoadCases=Data(new LoadCase("EX","LinearStatic",0,[new("Load","SX",1)]))});
        var table=new AnalysisEvidenceTable("Load Pattern Definitions - Auto Seismic - ASCE 7-16",
            ["Name","BaseShear","WeightUsed","CoeffUsed"],
            [new Dictionary<string,string>{{"Name","SX"},{"BaseShear","100"},{"WeightUsed","1000"},{"CoeffUsed","0.1"}}]);
        var analysis=new AnalysisResultsData(Data(new StoryDriftResult("L1","EX","Single Value",0,"X",.002,"A",0,0,3000)),
            Data(new ModalParticipationResult("Modal","Mode",1,.5,.6,.55,0,.6,.55,0,0,0,.1,0,0,.1),
                new ModalParticipationResult("Modal","Mode",2,.25,.35,.4,0,.95,.95,0,0,0,.1,0,0,.2)),
            Data(new BaseReactionResult("EX","Single Value",0,-100,0,-400,0,0,0,new(0,0,0))),[],[],[],[])
            {Extended=new([table],[],[],[],"ACI 318-19",true,[])};
        c=c with {Input=c.Input with {Analysis=new(null,analysis,null,[])}};
        var rule=Rule.Create("GEO-999","HTML fixture","Synthetic test criterion",[],_=>[
            new(CheckStatus.FAIL,"Unsafe <img src=x onerror=alert(1)> & details","F1","Frame",Story:"L1",Actual:2,Limit:1,Unit:"ratio",
                Evidence:[new("Evidence <script>","API <unsafe>","exact <value> & 0.002")]),
            new(CheckStatus.WARNING,"Review quotation \"data\"","F2"),
            new(CheckStatus.PASS,"Pass detail must not appear in attention table","F3"),
            new(CheckStatus.NOT_VERIFIED,"Missing evidence","F4")]);
        return new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,"all",[rule],[rule.Definition]);
    }
    private static void WithPath(Action<string> action)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"html-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"report.html");
        try {action(path);}
        finally {if(File.Exists(path))File.Delete(path); Directory.Delete(directory);}
    }
    [Fact]
    public void HtmlIsSelfContainedEscapedAndIncludesEveryStatusAndEvidence()
    {
        WithPath(path=>{
            HtmlReportGenerator.GenerateReport(Report(),path);
            var html=File.ReadAllText(path);
            Assert.Contains("Project &lt;script&gt;alert(1)&lt;/script&gt;",html);
            Assert.Contains("&lt;img src=x onerror=alert(1)&gt; &amp; details",html);
            Assert.DoesNotContain("<img src=x",html);
            Assert.DoesNotContain("<script>alert(1)</script>",html);
            Assert.Contains("<style>",html);Assert.Contains("<script>",html);
            Assert.DoesNotContain("<link",html);Assert.DoesNotContain("<script src=",html);
            Assert.Contains("data-status=\"FAIL\"",html);Assert.Contains("data-status=\"WARNING\"",html);
            Assert.Contains("data-status=\"PASS\"",html);
            Assert.Contains("data-status=\"NOT_VERIFIED\"",html);
            Assert.Contains("Pass detail must not appear",html);
            Assert.Contains("Evidence &lt;script&gt;",html);
            Assert.Contains("API &lt;unsafe&gt;",html);
            Assert.Contains("exact &lt;value&gt; &amp; 0.002",html);
            Assert.Contains("id=\"module-filter\"",html);Assert.Contains("id=\"finding-search\"",html);
            Assert.Contains("Calculation method &amp; formula",html);Assert.Contains("Recommendations",html);
            Assert.Contains("Total checks</span><strong>1</strong>",html);
            Assert.Contains("4 findings overall",html);
            Assert.Contains("@media print",html);Assert.Contains("window.print()",html);
        });
    }
    [Fact]
    public void AnalysisTablesShowUnitsModalTargetsAndMappedWeightWithoutInferringItFromFz()
    {
        WithPath(path=>{
            HtmlReportGenerator.GenerateReport(Report(),path);
            var html=File.ReadAllText(path);
            Assert.Contains("Frequency (Hz)",html);Assert.Contains("90% UX &amp; UY",html);
            Assert.Contains("95 %",html);Assert.Contains("Modal mass participation",html);
            Assert.Contains("<td>1000</td><td>0.1</td><td>100</td><td>0</td>",html);
            Assert.Contains("Weight W and Cs",html);Assert.Contains("Magnitude residual",html);
            Assert.Contains("<td>0.002</td><td>0.009</td><td>0.02</td>",html);
            Assert.Contains("Analysis evidence &amp; coverage",html);
        });
    }
    [Fact]
    public void FloorPlansAreEmbeddedInlineWithNoExternalFileDependency()
    {
        WithPath(path=>{
            HtmlReportGenerator.GenerateReport(Report(),path);
            var html=File.ReadAllText(path);
            Assert.Contains("id=\"story-selector\"",html);
            Assert.Contains("data-story-plan=\"0\"",html);
            Assert.Contains("<svg",html);Assert.Contains("#dc2626",html);
            Assert.Contains("story-0-plan-title",html);
            Assert.DoesNotContain("<iframe",html);Assert.DoesNotContain("<object",html);
            Assert.DoesNotContain("<img src=\"plans/",html);
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!,"plans")));
        });
    }
    [Fact]
    public void MissingAnalysisIsExplicitAndNeverCreatesFabricatedValues()
    {
        WithPath(path=>{
            var report=Report();
            HtmlReportGenerator.GenerateReport(report with {Input=report.Input with {Analysis=null}},path);
            var html=File.ReadAllText(path);
            Assert.Contains("Modal participation results unavailable.",html);
            Assert.Contains("Base reaction results unavailable.",html);
            Assert.Contains("Story drift results unavailable.",html);
            Assert.DoesNotContain("NaN",html);Assert.DoesNotContain("Infinity",html);
        });
    }
    [Fact]
    public void FindingsRetainStabilityValuesStandardsAndRecommendations()
    {
        WithPath(path=>{
            var report=Report();
            var stability=report.Findings[0] with {CheckId="SEIS-017",Module="SEIS",ElementId="L1/EX/X",Story="L1",
                Status=CheckStatus.PASS,Actual=.02,Limit=.111111,Unit="ratio",Standard="ASCE 7-16",CodeClause="12.8.7",
                Recommendations=["Review <gravity> basis"],PossibleCauses=["Unconfirmed load basis"],Title="Provisional stability calculation"};
            HtmlReportGenerator.GenerateReport(report with {Findings=[..report.Findings,stability]},path);
            var html=File.ReadAllText(path);
            Assert.Contains("<td>0.02</td><td>0.111111</td>",html);
            Assert.Contains("ASCE 7-16 · 12.8.7",html);
            Assert.Contains("Review &lt;gravity&gt; basis",html);
            Assert.Contains("Unconfirmed load basis",html);
            Assert.Contains("data-module=\"SEIS\"",html);
        });
    }
    [Fact]
    public void StorySelectorOrdersHighestElevationFirstAndIncludesEveryPlan()
    {
        WithPath(path=>{
            var report=Report();
            report=report with {Input=report.Input with {Model=report.Input.Model with
                {Stories=new(0,[new("L1",3000,3000,true,"None"),new("Roof",6000,3000,true,"None")])}}};
            HtmlReportGenerator.GenerateReport(report,path);
            var html=File.ReadAllText(path);
            Assert.Contains("<option value=\"0\">Roof</option><option value=\"1\">L1</option>",html);
            Assert.Contains("data-story-plan=\"1\" hidden",html);
            Assert.Contains("story-1-plan-title",html);
        });
    }
    [Fact]
    public void ZeroApiModalStepsAreResolvedUsingUniquePeriodEvidence()
    {
        WithPath(path=>{
            var report=Report();var result=report.Input.Analysis!.Results!;
            var periods=new AnalysisEvidenceTable("Modal Periods And Frequencies",["Case","Mode","Period","Frequency"],
                [new Dictionary<string,string>{{"Case","Modal"},{"Mode","7"},{"Period","0.500"},{"Frequency","1.999"}},
                 new Dictionary<string,string>{{"Case","Modal"},{"Mode","8"},{"Period","0.50"},{"Frequency","2.004"}}]);
            result=result with {ModalParticipation=Data(result.ModalParticipation.Items[0] with {StepNumber=0,Period=.5003}),
                Extended=result.Extended! with {Tables=[..result.Extended.Tables,periods]}};
            report=report with {Input=report.Input with {Analysis=report.Input.Analysis with {Results=result}}};
            HtmlReportGenerator.GenerateReport(report,path);
            Assert.Contains("<td>Modal</td><td>7</td><td>0.5003</td>",File.ReadAllText(path));
        });
    }
    [Fact]
    public void ExistingReportsAreNotOverwritten()
    {
        WithPath(path=>{
            File.WriteAllText(path,"existing report");
            Assert.Throws<IOException>(()=>HtmlReportGenerator.GenerateReport(Report(),path));
            Assert.Equal("existing report",File.ReadAllText(path));
        });
    }
    [Fact]
    public void ReportCommandAcceptsHtmlOutputAndConfig()
    {
        var options=CliOptions.Parse(["report","--format","html","--output","My Report.html","--config","criteria.json"]);
        Assert.Equal("report",options.Command);Assert.Equal("My Report.html",options.OutputPath);
        Assert.True(options.ExplicitConfiguration);Assert.Equal("criteria.json",options.ConfigurationPath);
    }
    [Fact]
    public void ReportCommandAcceptsSubmissionMode()
    {
        var options=CliOptions.Parse(["report","--format","html","--submission","--output","submission_note.html"]);
        Assert.True(options.Submission);
        Assert.Equal("submission_note.html",options.OutputPath);
    }
    [Fact]
    public void SubmissionModePrependsDraftSignoffSheetWithFindingDrivenMatrixAndLandscapePrint()
    {
        WithPath(path=>{
            var report=Report();
            var checks=new[]{("SEIS-004",CheckStatus.PASS),("SEIS-006",CheckStatus.FAIL),("AUD-002",CheckStatus.WARNING),("SEIS-017",CheckStatus.NOT_VERIFIED),("COL-004",CheckStatus.PASS)};
            var rows=checks.Select((x,i)=>report.Findings[0] with {Id="sub-"+i,CheckId=x.Item1,Module=x.Item1.Split('-')[0],Status=x.Item2,Title=x.Item1+" status"}).ToArray();
            HtmlReportGenerator.GenerateReport(report with {Findings=[..report.Findings,..rows]},path,submission:true);
            var html=File.ReadAllText(path);
            Assert.Contains("Structural Design &amp; Peer-Review Sign-Off Sheet",html);
            Assert.Contains("DRAFT · ENGINEER REVIEW AND SIGN-OFF REQUIRED",html);
            Assert.Contains("Engineer of Record",html);Assert.Contains("Peer reviewer",html);
            Assert.Contains("License #",html);Assert.Contains("professional stamp",html);
            Assert.Contains("Modal mass participation ≥ 90%",html);
            Assert.Contains("Base shear scaling ≥ 100%",html);
            Assert.Contains("This computer model has been inspected for structural integrity, code compliance, and numerical stability in accordance with ASCE 7-16 and ACI 318-19.",html);
            Assert.Contains("@page{size:landscape",html);
            Assert.True(html.IndexOf("submission-sheet",StringComparison.Ordinal)<html.IndexOf("Structural floor plans",StringComparison.Ordinal));
            Assert.Contains("SEIS-006: 1 finding(s)",html);
        });
    }
    [Theory]
    [InlineData("report","--format","pdf")]
    [InlineData("report","--format")]
    [InlineData("report","--output","model.edb")]
    [InlineData("report","--details")]
    [InlineData("inspect","all","--format","html")]
    public void InvalidReportArgumentsAreRejected(params string[] args)=>Assert.Throws<ConfigurationException>(()=>CliOptions.Parse(args));

    [Fact]
    public void GroupedFindingsKeepAffectedMembersAndReadLimitationsVisible()
    {
        WithPath(path =>
        {
            var report = Report();
            var group = report.Findings[0] with { Id = "group", CheckId = "STATE-DESIGN-COVERAGE", Module = "STATE", ElementId = null,
                ElementType = "FrameGroup", Kind = FindingKind.ApiUnavailable, AffectedElements = ["F1", "unsafe <member>"],
                RelatedCheckIds = ["BEAM-004", "BEAM-005"] };
            report = report with { Findings = [group], Coverage = [new("BEAM-004", VerificationState.VERIFIED, 0) { DeferredReason = "Design data could not be read." }] };
            HtmlReportGenerator.GenerateReport(report, path);
            var html = File.ReadAllText(path);
            Assert.Contains("Model state", html);
            Assert.Contains("Affected members (2)", html);
            Assert.Contains("unsafe &lt;member&gt;", html);
            Assert.Contains("Data could not be read", html);
            Assert.Contains("Design data could not be read.", html);
            Assert.Equal(new[] { "Frame:F1" }, Program.FailedElementIds(report));
        });
    }
}
