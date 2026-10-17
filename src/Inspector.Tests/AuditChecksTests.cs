public sealed class AuditChecksTests
{
    private static InspectionContext WithAudit(InspectionContext c, ExtendedAnalysisEvidence? ext = null,
        IReadOnlyList<BaseReactionResult>? reactions = null, IReadOnlyList<StoryDriftResult>? drifts = null,
        IReadOnlyList<StoryForceResult>? forces = null, IReadOnlyList<GeometricNonlinearitySetting>? nonlinear = null,
        IReadOnlyList<ResponseSpectrumCase>? spectra = null, IReadOnlyList<LoadCombination>? combos = null,
        IReadOnlyList<AnalysisCaseStatus>? statuses = null)
    {
        c = c.Settings(x => x with
        {
            SeismicVerification = new("ASCE 7-16", 4.5, 1, .02, 1, 1,
                [new("X", "EQX", "RSX"), new("Y", "EQY", "RSY")], [new("D", 1)], true)
        });
        c = c.Validation(v => v with
        {
            LoadCases = Data(new LoadCase("EQX", "LinearStatic", 0, null), new LoadCase("EQY", "LinearStatic", 0, null),
                new LoadCase("RSX", "ResponseSpectrum", 0, null), new LoadCase("RSY", "ResponseSpectrum", 0, null), new LoadCase("D", "LinearStatic", 0, null),new LoadCase("PD","NonlinearStatic",0,null)),
            Combinations = Data((combos ?? [new LoadCombination("EQX-ULS", 0, [new("LoadCase", "EQX", 0, 1)])]).ToArray())
        });
        var result = new AnalysisResultsData(Data(drifts?.ToArray() ?? []), Data<ModalParticipationResult>(),
            Data(reactions?.ToArray() ?? []), [], [], [], [])
        {
            StoryForces = forces is null ? null : Data(forces.ToArray()),
            Extended = ext
        };
        var definitions = new AnalysisDefinitionData(Data(spectra?.ToArray() ?? []), Data(nonlinear?.ToArray() ?? []));
        return c with { Input = c.Input with { Analysis = new(new(Data(statuses?.ToArray() ?? [])), result, definitions, []) } };
    }
    private static ExtendedAnalysisEvidence ModifierEvidence(params ModifierEvidence[] modifiers) => new([], modifiers, [], [], null, null, []);

    [Fact]
    public void CrackedModifierScreenRecognizesRangesUncrackedAndDoubleReduction()
    {
        var c = Context().Model(m => m with
        {
            Frames = Data(new Frame("B1", "L1", "Beam", "A", "B", new(0,0,3000), new(3000,0,3000), 0),
                new Frame("C1", "L1", "Column", "A", "B", new(0,0,0), new(0,0,3000), 0)),
            FrameSections = Data(new FrameSection("Beam", "Rect", "C25", 500, 300, null, null),
                new FrameSection("Column", "Rect", "C25", 500, 500, null, null))
        });
        var ext = ModifierEvidence(
            new("FrameObj", "B1", [.5,1,1,1,.7,.7,1,1]), new("PropFrame", "Beam", [1,1,1,1,.5,.5,1,1]),
            new("FrameObj", "C1", [1,1,1,1,1,1,1,1]), new("PropFrame", "Column", [1,1,1,1,1,1,1,1]));
        c = WithAudit(c, ext);
        var findings = AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-001").Evaluate(c);
        Assert.Contains(findings, x=>x.ElementId=="B1/I22"&&x.Status==CheckStatus.PASS&&x.Actual==.35);
        Assert.Contains(findings, x=>x.ElementId=="C1/I22"&&x.Status==CheckStatus.WARNING&&x.Actual==1);
        Assert.Contains(findings, x=>x.Title.Contains("Double-reduced beam")&&x.Actual==.35);
    }

    [Theory]
    [InlineData(.35)]
    [InlineData(.70)]
    public void WallModifierScreenAcceptsEitherCrackedOrUncrackedAndStandardF12(double modifier)
    {
        var c=Context().Model(m=>m with
        {
            Frames=Data<Frame>(),
            Areas=Data(new Area("W1","P1","L1","SW","Wall",false,["A","B","C","D"])),
            AreaSections=Data(new AreaSection("SW","Wall","ShellThick","C25",200,null))
        });
        var values=new[]{modifier,modifier,1d,modifier,modifier,1d,1d,1d,1d,1d};
        c=WithAudit(c,ModifierEvidence(new("AreaObj","W1",values),new("PropArea","SW",new[]{1d,1d,1d,1d,1d,1d,1d,1d,1d,1d})));
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-001").Evaluate(c);
        Assert.Equal(6,findings.Count);
        Assert.All(findings, finding=>Assert.Equal(CheckStatus.PASS,finding.Status));
        Assert.Contains(findings,finding=>finding.ElementId=="W1/f12"&&finding.Actual==1);
        Assert.Contains(findings,finding=>finding.ElementId=="W1/m12"&&finding.Actual==1);
    }

    [Theory]
    [InlineData(0.86, CheckStatus.FAIL, 1.163)]
    [InlineData(1.00, CheckStatus.PASS, 1.000)]
    [InlineData(1.08, CheckStatus.PASS, 0.926)]
    public void SpectrumScalingUsesOneHundredPercentElfBoundary(double ratio, CheckStatus status, double multiplier)
    {
        var c=WithAudit(Context(), reactions:new BaseReactionResult[]{
            new("EQX","Single Value",0,1000,0,0,0,0,0,new(0,0,0)),new("RSX","Max",0,1000*ratio,0,0,0,0,0,new(0,0,0)),
            new("EQY","Single Value",0,0,1000,0,0,0,0,new(0,0,0)),new("RSY","Max",0,0,1000,0,0,0,0,new(0,0,0))},
            spectra:new ResponseSpectrumCase[]{new("RSX","MODAL",0,0,0,0,0,0,0,[new("U1","F",1,"Global",0)]),new("RSY","MODAL",0,0,0,0,0,0,0,[new("U2","F",1,"Global",0)])},
            statuses:new[]{"EQX","RSX","EQY","RSY"}.Select(x=>new AnalysisCaseStatus(x,AnalysisCaseState.Finished,4)).ToArray());
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-002").Evaluate(c);
        var x=Assert.Single(findings,x=>x.ElementId=="X");
        Assert.Equal(status,x.Status);Assert.Equal(ratio,x.Actual!.Value,6);
        if (status == CheckStatus.FAIL) Assert.Contains($"{multiplier:F3}",x.Title);
    }

    [Theory]
    [InlineData(1.2, 0.05)]
    [InlineData(1.5, 0.078125)]
    [InlineData(2.5, 0.15)]
    public void TorsionalAmplificationUsesAxFormulaAndThreeCap(double ratio,double expectedEccentricity)
    {
        var torsion=new AnalysisEvidenceTable("Diaphragm Max Over Avg Drifts",["Story","OutputCase","Item","Ratio"],
            [new Dictionary<string,string>{{"Story","L1"},{"OutputCase","EQX"},{"Item","Drift X"},{"Ratio",ratio.ToString(CultureInfo.InvariantCulture)}}]);
        var auto=new AnalysisEvidenceTable("Load Pattern Definitions - Auto Seismic - ASCE 7-16",["Name","EccRatio","XDir","XDirPlusE"],
            [new Dictionary<string,string>{{"Name","SX"},{"EccRatio","0.05"},{"XDir","Yes"},{"XDirPlusE","Yes"}}]);
        var c=WithAudit(Context(),ext:new([torsion,auto],[],[],[],null,null,[]));
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-003").Evaluate(c);
        var x=Assert.Single(findings,x=>x.ElementId!.EndsWith("/X",StringComparison.Ordinal));
        Assert.Equal(ratio>1.2+1e-9?CheckStatus.WARNING:CheckStatus.PASS,x.Status);
        if(ratio>1.2) Assert.Equal(expectedEccentricity,x.Limit!.Value,6);
    }

    [Fact]
    public void PDeltaAuditWarnsWhenThetaExceedsPointOneWithoutCombinationLineage()
    {
        StoryDriftResult[] drift=[new StoryDriftResult("L1","EQX","Single Value",0,"X",.02,"A",0,0,3000)];
        var forces=new[]{new StoryForceResult("L1","EQX","Single Value",0,"Bottom",0,100,0),
            new StoryForceResult("L1","D","Single Value",0,"Bottom",1000,0,0)};
        var c=WithAudit(Context(),drifts:drift,forces:forces,nonlinear:[new("PD","StaticNonlinear",1,"P-Delta")]);
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-004").Evaluate(c);
        Assert.Contains(findings,x=>x.Status==CheckStatus.WARNING&&x.Actual==.2);
    }

    [Fact]
    public void PDeltaAuditPassesWhenLateralCombinationContainsActiveNonlinearCase()
    {
        StoryDriftResult[] drift=[new StoryDriftResult("L1","EQX","Single Value",0,"X",.02,"A",0,0,3000)];
        var forces=new[]{new StoryForceResult("L1","EQX","Single Value",0,"Bottom",0,100,0),
            new StoryForceResult("L1","D","Single Value",0,"Bottom",1000,0,0)};
        LoadCombination[] combos=[new LoadCombination("EQX-ULS",0,[new("LoadCase","EQX",0,1),new("LoadCase","PD",0,1)])];
        var c=WithAudit(Context(),drifts:drift,forces:forces,nonlinear:[new("PD","StaticNonlinear",1,"P-Delta")],combos:combos);
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-004").Evaluate(c);
        Assert.Contains(findings,x=>x.Status==CheckStatus.PASS&&x.Actual==.2);
    }

    [Fact]
    public void PDeltaAuditUsesMaxMinSeismicEnvelopeWithSingleValueGravityRows()
    {
        StoryDriftResult[] drift=[new StoryDriftResult("L1","EQX","Max",0,"X",-.02,"A",0,0,3000),new("L1","EQX","Min",0,"X",.01,"A",0,0,3000)];
        var forces=new[]{new StoryForceResult("L1","EQX","Max",0,"Bottom",0,-100,0),new("L1","EQX","Min",0,"Bottom",0,80,0),new("L1","D","Single Value",0,"Bottom",1000,0,0)};
        var c=WithAudit(Context(),drifts:drift,forces:forces,nonlinear:[new("PD","StaticNonlinear",1,"P-Delta")]);
        var findings=AuditChecks.Create().Single(x=>x.Definition.Id=="AUD-004").Evaluate(c);
        Assert.Contains(findings,x=>x.Status==CheckStatus.WARNING&&x.Actual==.2);
    }
}
