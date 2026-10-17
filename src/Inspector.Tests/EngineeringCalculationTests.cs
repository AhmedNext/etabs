using static EngineeringCalculations;

public sealed class EngineeringCalculationTests
{
    // CSI Software Verification Example 1, PDF pp.21-23, Table 1-1 Case II.
    // Compatibility of the propped cantilever gives R_pin=3wL/8; equilibrium gives the other reaction and fixed-end moment.
    [Fact]
    public void CsiExampleOneUniformBeamMatchesIndependentCompatibilitySolution()
    {
        const double length=216, w=10.0/12;
        var (pin,fixedEnd,moment)=ProppedCantileverUniform(w,length);
        Assert.Equal(67.5,pin,10);Assert.Equal(112.5,fixedEnd,10);Assert.Equal(-4860,moment,10);
        Assert.Equal(w*length,pin+fixedEnd,10);
        Assert.Equal(2430,pin*(length/4)-w*Math.Pow(length/4,2)/2,10);
        Assert.Equal(2430,pin*(length/2)-w*Math.Pow(length/2,2)/2,10);
        Assert.Equal(0,pin*(3*length/4)-w*Math.Pow(3*length/4,2)/2,10);
    }
    [Fact]
    public void ThreeStoryEigenvaluesSatisfyAnIndependentlyExpandedDeterminant()
    {
        var periods=EqualStoryShearBuildingPeriods(3,1000,1);
        foreach(var t in periods)
        {
            var lambda=Math.Pow(2*Math.PI/t,2)/1000;
            // det(K-lambda I), K=[[2,-1,0],[-1,2,-1],[0,-1,1]].
            Assert.InRange(Math.Abs(lambda*lambda*lambda-5*lambda*lambda+6*lambda-1),0,1e-12);
        }
    }
    [Fact]
    public void CsiExampleTwoEqualStoryPeriodRatiosMatchPublishedModes()
    {
        // CSI Example 2 Table 2-1: T1=.4414, T2=.1575, T3=.1090 seconds.
        // Compare ratios: no unprovided W14x90 section inertia is invented, and no claim is made for an absolute model stiffness.
        var normalized=EqualStoryShearBuildingPeriods(3,1,1);
        Assert.InRange(Math.Abs(.4414*normalized[1]/normalized[0]-.1575),0,.0001);
        Assert.InRange(Math.Abs(.4414*normalized[2]/normalized[0]-.1090),0,.0001);
    }
    [Fact]
    public void HandSolvedDriftMassAndStabilityUseConsistentUnits()
    {
        Assert.Equal(.002,Drift(.012,.006,3),12);
        Assert.Equal(.05,StabilityTheta(1000,.015,100,3),12);
        Assert.Equal(100,980.665/Gravity("m"),10);
        Assert.Equal(100,980665/Gravity("mm"),10);
        Assert.Equal(Gravity("in"),Gravity("inch"),12); // Installed ETABS enum spelling.
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void MissingHeightCannotProduceADriftOrStabilityPass(double height)
    {
        Assert.Throws<InvalidOperationException>(()=>Drift(1,0,height));
        Assert.Throws<InvalidOperationException>(()=>StabilityTheta(1,1,1,height));
    }
    [Fact]
    public void DisplayPrecisionIsPropagatedRatherThanTreatedAsACodeLimit()
    {
        var a=Displayed("1.203");Assert.Equal(.0005,a.Error,12);
        Assert.True(a.Agrees(new(1.2034)));Assert.False(a.Agrees(new(1.204)));
        Assert.Throws<InvalidOperationException>(()=>Displayed(""));
    }
    [Fact]
    public void PolygonAreaHandlesConcavityButRejectsWarpedSurfaces()
    {
        Assert.Equal(3,PolygonArea([new(0,0,0),new(2,0,0),new(2,1,0),new(1,1,0),new(1,2,0),new(0,2,0)]),10);
        Assert.Throws<InvalidOperationException>(()=>PolygonArea([new(0,0,0),new(2,0,0),new(2,2,1),new(0,2,0)]));
    }
    private static AnalysisEvidenceTable Table(string key,params Dictionary<string,string>[] rows)=>new(key,rows.SelectMany(r=>r.Keys).Distinct().ToArray(),rows);
    private static Dictionary<string,string> Row(params (string Key,string Value)[] values)=>values.ToDictionary(v=>v.Key,v=>v.Value);
    private static InspectionContext Fixture()
    {
        var c=Context();
        string[] empty=["Area Load Assignments - Uniform","Frame Loads Assignments - Distributed","Joint Loads Assignments - Force","Joint Loads Assignments - Ground Displacement",
            "Joint Assignments - Additional Mass","Frame Assignments - Additional Mass","Area Assignments - Additional Mass"];
        var tables=new[]{
            Table("Mass Source Definition",Row(("Name","M"),("IncLateral","Yes"),("IncVertical","Yes"))),
            Table("Mass Summary by Group",Row(("Group","All"),("MassX",(200/Gravity("mm")).ToString("G17",CultureInfo.InvariantCulture)),("MassY",(200/Gravity("mm")).ToString("G17",CultureInfo.InvariantCulture))))
        };
        var inventory=empty.Select(k=>new AnalysisTableSnapshot(k,k,true,[],0)).Concat(tables.Select(t=>new AnalysisTableSnapshot(t.Key,t.Key,false,t.Fields,t.Rows.Count))).ToArray();
        var modifiers=new[]{new ModifierEvidence("FrameObj","F1",Enumerable.Repeat(1d,8).ToArray()),new("PropFrame","B",Enumerable.Repeat(1d,8).ToArray()),
            new("AreaObj","S1",Enumerable.Repeat(1d,10).ToArray()),new("PropArea","S",Enumerable.Repeat(1d,10).ToArray())};
        var results=new AnalysisResultsData(Data<StoryDriftResult>(),Data<ModalParticipationResult>(),Data(new BaseReactionResult("Dcase","Single Value",0,0,0,200,0,0,0,new(0,0,0))),[],[],[],[])
            {VerificationEvidence=new(inventory,tables,true,[]){SimpleFrameWeightGeometryVerified=true,GravityLoads=Data<GravityLoadAssignment>()},Extended=new([],modifiers,[],[],null,null,[])};
        return c with{Input=c.Input with{
            Model=c.Input.Model with{Materials=Data(c.Input.Model.Materials!.Items[0] with{UnitWeight=200/(600d*300*3000+3000d*3000*200)})},
            MaterialTakeoffs=Data(new MaterialTakeoffResult("Frame","C25",200,100)),
            Validation=c.Input.Validation with{LoadCases=Data(new LoadCase("Dcase","LinearStatic",0,[new("Load","D",1)])),
                MassSources=Data(new MassSource("M",true,false,false,true,[new("D",1)]))},
            Analysis=new(new(Data(new AnalysisCaseStatus("Dcase",AnalysisCaseState.Finished,4))),results,null,[])
        }};
    }
    private static IReadOnlyList<Finding> Run(string id,InspectionContext c)=>IndependentAnalysisChecks.Create().Single(x=>x.Definition.Id==id).Evaluate(c);
    [Fact]
    public void IndependentGravityAndMassChecksNeedNoConcreteDesignResults()
    {
        var c=Fixture();
        Assert.Null(c.Input.ConcreteColumns);
        Assert.Equal(CalculationValidation.IndependentLoadBalance,Assert.Single(Run("ANA-101",c)).CalculationValidation);
        Assert.Contains(Run("ANA-103",c),f=>f.CalculationValidation==CalculationValidation.IndependentLoadBalance);
        Assert.Contains(Run("ANA-103",c),f=>f.CalculationValidation==CalculationValidation.ResultConsistency);
    }
    [Fact]
    public void FailedApiReactionCanBeRecoveredFromACompleteScreenTable()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;var evidence=result.VerificationEvidence!;
        var raw=Table("Base Reactions",Row(("OutputCase","Dcase"),("CaseType","LinStatic"),("FX","0.0000"),("FY","0.0000"),("FZ","0.2000"),("MX","0.0000"),("MY","0.0000"),("MZ","0.0000"),("X","0.0000"),("Y","0.0000"),("Z","0.0000")));
        var recovered=ResultTableRecovery.Normalize(raw,new("kN","m","C"),c.Input.Model.Identity.DisplayUnits,"UI fixture",DateTimeOffset.UtcNow);
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{BaseReactions=result.BaseReactions with{ReadSucceeded=false},VerificationEvidence=evidence with{Tables=[..evidence.Tables,recovered]}}}}};
        var reaction=new VerificationInputs(c).Reaction("Dcase");
        Assert.Equal(200,reaction.FZ,10);Assert.Equal(.05,reaction.ForceUncertainty,10);Assert.Equal("UI fixture",reaction.RecoverySource);
        var finding=Assert.Single(Run("ANA-101",c));
        Assert.Equal(CheckStatus.PASS,finding.Status);Assert.Equal(CalculationValidation.IndependentLoadBalance,finding.CalculationValidation);
    }
    [Fact]
    public void StoryForceRecoveryRetainsNonStaticRowsWithoutTreatingThemAsSimultaneous()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;var evidence=result.VerificationEvidence!;
        var table=Table("Story Forces",Row(("Story","L1"),("OutputCase","Dcase"),("CaseType","LinStatic"),("Location","Bottom"),("P","200"),("VX","10"),("VY","5")),
            Row(("Story","L1"),("OutputCase","RS"),("CaseType","LinRespSpec"),("Location","Bottom"),("P","200"),("VX","10"),("VY","5"))) with{RecoverySource="export"};
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{StoryForces=Data<StoryForceResult>() with{ReadSucceeded=false},VerificationEvidence=evidence with{Tables=[..evidence.Tables,table]}}}}};
        var recovered=new VerificationInputs(c).StoryForces();
        Assert.Equal("Single Value",recovered.Single(r=>r.Case=="Dcase").StepType);
        Assert.Equal("Unspecified",recovered.Single(r=>r.Case=="RS").StepType);
    }
    [Fact]
    public void FailedReactionReadIsUnavailableDataRatherThanAMissingModelInput()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{BaseReactions=result.BaseReactions with{ReadSucceeded=false}}}}};
        var finding=Assert.Single(Run("ANA-101",c));
        Assert.Equal(FindingKind.ApiUnavailable,finding.Kind);
        Assert.StartsWith("Data could not be read:",finding.Title);
        Assert.Equal(CheckStatus.NOT_VERIFIED,finding.Status);
    }
    [Fact]
    public void FailedTableReadIsUnavailableDataRatherThanAMissingAssignment()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;var evidence=result.VerificationEvidence!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=evidence with{Tables=[..evidence.Tables,new("Joint Reactions",[],[],"ETABS returned code 1")]}}}}};
        var finding=Assert.Single(Run("ANA-102",c));
        Assert.Equal(FindingKind.ApiUnavailable,finding.Kind);
        Assert.StartsWith("Data could not be read:",finding.Title);
    }
    [Fact]
    public void IncorrectReactionIsNotAPassAndSignedLoadsAreRetained()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{BaseReactions=Data(result.BaseReactions.Items[0] with{FZ=-200})}}}};
        var finding=Assert.Single(Run("ANA-101",c));
        Assert.Equal(CheckStatus.WARNING,finding.Status);Assert.Equal(CalculationValidation.Unverified,finding.CalculationValidation);
    }
    [Theory]
    [InlineData("ANA-101")]
    [InlineData("ANA-102")]
    [InlineData("ANA-103")]
    [InlineData("ANA-104")]
    [InlineData("ANA-105")]
    [InlineData("ANA-106")]
    [InlineData("ANA-107")]
    public void EssentialMissingInputsAreExplicitAndNeverPass(string id)
    {
        var c=id=="ANA-106"?Context().Validation(v=>v with{LoadCases=Data(new LoadCase("M","Modal",0,null))}):Context();
        var findings=Run(id,c);
        Assert.All(findings,f=>{Assert.NotEqual(CheckStatus.PASS,f.Status);Assert.StartsWith("Missing input:",f.Title);});
    }
    [Fact]
    public void UnknownAssignmentInventoryAndAddedMassCannotBeAssumedAbsent()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;var evidence=result.VerificationEvidence!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=evidence with{InventoryComplete=false}}}}};
        Assert.StartsWith("Missing input:",Assert.Single(Run("ANA-101",c)).Title);
        c=Fixture().Validation(v=>v with{MassSources=Data(new MassSource("M",true,false,true,true,[new("D",1)]))});
        result=c.Input.Analysis!.Results!;evidence=result.VerificationEvidence!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=evidence with{Inventory=evidence.Inventory.Where(t=>t.Key!="Joint Assignments - Additional Mass").ToArray()}}}}};
        Assert.All(Run("ANA-103",c),f=>Assert.StartsWith("Missing input:",f.Title));
    }
    [Fact]
    public void ModalIdentityDoesNotClaimIndependentStiffnessValidation()
    {
        var c=Fixture().Validation(v=>v with{LoadCases=Data(new LoadCase("M","Modal",0,null))});
        var result=c.Input.Analysis!.Results!;var e=result.VerificationEvidence!;
        var modal=Table("Modal Periods And Frequencies",Row(("Case","M"),("Mode","1"),("Period",Math.PI.ToString("G17",CultureInfo.InvariantCulture)),("Eigenvalue","4.000000000")));
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Status=new(Data(new AnalysisCaseStatus("M",AnalysisCaseState.Finished,4))),Results=result with{VerificationEvidence=e with{Tables=[..e.Tables,modal]}}}}};
        var findings=Run("ANA-106",c);
        Assert.Contains(findings,f=>f.CalculationValidation==CalculationValidation.ResultConsistency);
        Assert.Contains(findings,f=>f.Title.Contains("assembled stiffness",StringComparison.Ordinal)&&f.Status==CheckStatus.NOT_VERIFIED);
        Assert.DoesNotContain(findings,f=>f.CalculationValidation==CalculationValidation.IndependentLoadBalance);
    }

    [Fact]
    public void RectangularOpeningsAreClippedAtPanelEdgesAndOverlapsAreNotCountedTwice()
    {
        var c=Fixture().Model(m=>m with{
            Joints=Data([..m.Joints!.Items,new("H1",new(2000,1000,3000)),new("H2",new(4000,1000,3000)),new("H3",new(4000,2000,3000)),new("H4",new(2000,2000,3000))]),
            Areas=Data([..m.Areas!.Items,new("O1","O1","L1",null,"Null",true,["H1","H2","H3","H4"]),new("O2","O2","L1",null,"Null",true,["H1","H2","H3","H4"])])});
        Assert.Equal(8_000_000,new VerificationInputs(c).NetArea(c.Input.Model.Areas!.Items[0]),8);
    }
    [Fact]
    public void CoarseDefinitionTableTextCannotHideAReactionDiscrepancy()
    {
        var c=Fixture().Validation(v=>v with{LoadPatterns=Data(new LoadPattern("D","Dead",0))});
        var result=c.Input.Analysis!.Results!;var e=result.VerificationEvidence!;
        var areaTable=Table("Area Load Assignments - Uniform",Row(("LoadPattern","D"),("Load","0")));
        e=e with{Tables=[..e.Tables,areaTable],Inventory=e.Inventory.Select(t=>t.Key==areaTable.Key?t with{IsEmpty=false}:t).ToArray(),
            GravityLoads=Data(new GravityLoadAssignment("Area","S1","D",1,"Global",10,0,0,2d/9_000_000,2d/9_000_000))};
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=e,BaseReactions=Data(result.BaseReactions.Items[0] with{FZ=2.1})}}}};
        var finding=Assert.Single(Run("ANA-101",c));
        Assert.Equal(CheckStatus.WARNING,finding.Status);Assert.Equal(2,finding.Limit!.Value,10);
    }
    [Fact]
    public void AutomaticOffsetsBlockUnverifiedSelfWeightInsteadOfGuessingACorrection()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=result.VerificationEvidence! with{
            SimpleFrameWeightGeometryVerified=false,GeometryLimitations=["Automatic end offsets require unsupported overlap calculations."]}}}}};
        var finding=Assert.Single(Run("ANA-101",c));
        Assert.StartsWith("Inspector limitation:",finding.Title);Assert.Equal(FindingKind.ReviewRequired,finding.Kind);
    }
    [Fact]
    public void ReactionMomentsAreTransferredToTheActualBaseReferencePoint()
    {
        var c=Fixture().Model(m=>m with{Joints=Data(new Joint("A",new(2,3,4)))});
        var table=Table("Joint Reactions",Row(("UniqueName","A"),("OutputCase","Dcase"),("StepType",""),("StepNumber",""),
            ("FX","5.000000000"),("FY","7.000000000"),("FZ","11.000000000"),("MX","13.000000000"),("MY","17.000000000"),("MZ","19.000000000")));
        var result=c.Input.Analysis!.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{
            BaseReactions=Data(new BaseReactionResult("Dcase","Single Value",0,5,7,11,14,21,16,new(1,1,1))),
            VerificationEvidence=result.VerificationEvidence! with{Tables=[..result.VerificationEvidence.Tables,table]}}}}};
        Assert.Equal(CheckStatus.PASS,Assert.Single(Run("ANA-102",c)).Status);
        result=c.Input.Analysis.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{BaseReactions=Data(result.BaseReactions.Items[0] with{MZ=22})}}}};
        Assert.Equal(CheckStatus.WARNING,Assert.Single(Run("ANA-102",c)).Status);
    }
    [Fact]
    public void RepetitiveUnrunCasesProduceOneInformationalMessage()
    {
        var c=Fixture().Validation(v=>v with{LoadCases=Data(Enumerable.Range(0,1000).Select(i=>new LoadCase("C"+i,"LinearStatic",0,[])).ToArray())});
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis! with{Status=new(Data(c.Input.Validation.LoadCases!.Items.Select(x=>new AnalysisCaseStatus(x.Name,AnalysisCaseState.NotRun,1)).ToArray()))}}};
        var finding=Assert.Single(Run("ANA-101",c));Assert.Equal(FindingKind.OperationNotPerformed,finding.Kind);Assert.Contains("1000",finding.Title);
    }
    [Fact]
    public void UnreadUnsupportedPatternsWithDifferentNamesAreGroupedByMissingCapability()
    {
        var c=Fixture().Validation(v=>v with{
            LoadCases=Data(Enumerable.Range(0,200).Select(i=>new LoadCase("Case"+i,"LinearStatic",0,[new("Load","Pattern"+i,1)])).ToArray()),
            LoadPatterns=Data(Enumerable.Range(0,200).Select(i=>new LoadPattern("Pattern"+i,"Other",0)).ToArray())});
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis! with{Status=new(Data(c.Input.Validation.LoadCases!.Items.Select(x=>new AnalysisCaseStatus(x.Name,AnalysisCaseState.Finished,4)).ToArray()))}}};
        var finding=Assert.Single(Run("ANA-101",c));Assert.Contains("200 cases",finding.Title);Assert.NotEqual(CheckStatus.PASS,finding.Status);
    }
    [Fact]
    public void ReactionBasedMassCanBeConsistentWhileIndependentMassRemainsUnverified()
    {
        var c=Fixture();var result=c.Input.Analysis!.Results!;
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Results=result with{VerificationEvidence=result.VerificationEvidence! with{SimpleFrameWeightGeometryVerified=false}}}}};
        var findings=Run("ANA-103",c);
        Assert.Contains(findings,f=>f.CalculationValidation==CalculationValidation.ResultConsistency);
        Assert.Contains(findings,f=>f.Kind==FindingKind.ReviewRequired&&f.Title.StartsWith("Inspector limitation:",StringComparison.Ordinal));
        Assert.DoesNotContain(findings,f=>f.CalculationValidation==CalculationValidation.IndependentLoadBalance);
    }
    [Fact]
    public void ElementMassUsesMassDensityAndDoesNotInferItFromWeightDensity()
    {
        var c=Fixture();
        var expected=(600d*300*3000+3000d*3000*200)*c.Input.Model.Materials!.Items[0].MassDensity;
        Assert.Equal(expected,new VerificationInputs(c).ElementMass().Value,10);
        Assert.NotEqual(new VerificationInputs(c).SelfWeight().Value/Gravity("mm"),expected);
    }
    [Fact]
    public void NegativeEigenvaluesAreReportedAsAnObservedConditionNotMissingAnalysis()
    {
        var c=Fixture().Validation(v=>v with{LoadCases=Data(new LoadCase("M","Modal",0,null))});
        var result=c.Input.Analysis!.Results!;
        var table=Table("Modal Periods And Frequencies",Row(("Case","M"),("Mode","1"),("Period","1.000000"),("Eigenvalue","-4.000000")));
        c=c with{Input=c.Input with{Analysis=c.Input.Analysis with{Status=new(Data(new AnalysisCaseStatus("M",AnalysisCaseState.Finished,4))),Results=result with{VerificationEvidence=result.VerificationEvidence! with{Tables=[..result.VerificationEvidence.Tables,table]}}}}};
        var findings=Run("ANA-106",c);
        Assert.Contains(findings,f=>f.Status==CheckStatus.WARNING&&f.Title.Contains("nonpositive modal eigenvalues",StringComparison.Ordinal));
        Assert.DoesNotContain(findings,f=>f.Status==CheckStatus.PASS);
    }
}
