public sealed class IndependentAnalysisLiveTests
{
    [LiveEtabsFact]
    public Task OpenResultTableFallbackReadsTheDisplayedGridWithoutInput() => StaRunner.Run(() =>
    {
        using var session=EtabsSession.Attach(NullLogger.Instance);
        var identity=session.ReadIdentity();
        var tables=OpenResultTableReader.Read(identity);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"open-table-fallback.json"),JsonSerializer.Serialize(tables,new JsonSerializerOptions{WriteIndented=true}));
        Assert.NotEmpty(tables);
        Assert.All(tables,t=>Assert.NotNull(t.RecoverySource));
        Assert.True(ModelConsistency.SameContext(identity,session.ReadIdentity()));
        return true;
    });
    [LiveEtabsFact]
    public Task DiagnoseResultRetrievalReadOnly() => StaRunner.Run(() =>
    {
        using var session=EtabsSession.Attach(NullLogger.Instance);
        var model=session.ModelForVerification;
        int n=0;string[] cases=[];
        var caseCode=model.DatabaseTables.GetLoadCasesSelectedForDisplay(ref n,ref cases);
        int nc=0;string[] combos=[];
        var comboCode=model.DatabaseTables.GetLoadCombinationsSelectedForDisplay(ref nc,ref combos);
        var probes=new List<object>();
        foreach(var key in new[]{"Joint Reactions","Joint Displacements","Story Forces","Base Reactions"})
        foreach(var group in new[]{"All",""})
        {
            string[] requested=[""],fields=[],data=[];int version=0,count=0;
            int code=model.DatabaseTables.GetTableForDisplayArray(key,ref requested,group,ref version,ref fields,ref count,ref data);
            probes.Add(new{key,group,code,count,fields,sample=data.Take(30)});
            requested=[""];string csv="";version=0;
            int csvCode=model.DatabaseTables.GetTableForDisplayCSVString(key,ref requested,group,ref version,ref csv,",");
            probes.Add(new{key,group,method="CSVString",code=csvCode,length=csv.Length,sample=csv[..Math.Min(500,csv.Length)]});
        }
        int pn=0;string[] points=[];model.PointObj.GetNameList(ref pn,ref points);
        foreach(var item in new[]{("All",eItemTypeElm.GroupElm),(points.First(),eItemTypeElm.ObjectElm)})
        {
            int count=0;string[] obj=[],elm=[],lc=[],step=[];double[] sn=[],u1=[],u2=[],u3=[],r1=[],r2=[],r3=[];
            int code=model.Results.JointDispl(item.Item1,item.Item2,ref count,ref obj,ref elm,ref lc,ref step,ref sn,ref u1,ref u2,ref u3,ref r1,ref r2,ref r3);
            probes.Add(new{method="JointDispl",name=item.Item1,code,count});
        }
        var results=session.ReadSelectedAnalysisResults();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"result-retrieval-diagnostic.json"),JsonSerializer.Serialize(new{caseCode,cases,comboCode,combos,probes,results},new JsonSerializerOptions{WriteIndented=true}));
        return true;
    });

    [LiveEtabsFact]
    public Task ValidateEngineeringCalculationsAgainstTheOpenModelReadOnly() => StaRunner.Run(() =>
    {
        using var session=EtabsSession.Attach(NullLogger.Instance);
        var before=session.ReadIdentity();
        var statuses=JsonSerializer.Serialize(session.ReadAnalysisStatus().Cases.Items);
        var selected=Selection();
        using var modelFile=new FileStream(before.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var hash=Convert.ToHexString(SHA256.HashData(modelFile));
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src","Inspector.sln")))root=root.Parent;
        Assert.NotNull(root);
        var configuration=ProjectConfiguration.Load(Path.Combine(root.FullName,"src","config","project.asce7-16.json"));
        var input=new InspectionExtractor(new(NullLogger<ModelExtractor>.Instance),NullLogger<InspectionExtractor>.Instance)
            .Extract(session,readAnalysis:true,configuration:configuration);
        var checks=IndependentAnalysisChecks.Create();
        var report=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(input,configuration,"analysis",checks,checks.Select(c=>c.Definition));
        var identityUnchanged=ModelConsistency.SameContext(before,session.ReadIdentity());
        var statusUnchanged=statuses==JsonSerializer.Serialize(session.ReadAnalysisStatus().Cases.Items);
        var selectionUnchanged=selected==Selection();
        modelFile.Position=0;
        var fileUnchanged=hash==Convert.ToHexString(SHA256.HashData(modelFile));
        var path=Path.Combine(root.FullName,"docs","reports","session2-validation-live.json");
        var reportPath=Path.Combine(root.FullName,"docs","reports","session2-engineering-validation-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff",CultureInfo.InvariantCulture)+".html");
        HtmlReportGenerator.GenerateReport(DiagnosisEngine.Enrich(report),reportPath);
        File.WriteAllText(path,JsonSerializer.Serialize(new{model=before,report.Findings,report.Coverage,
            reportPath,
            outputSelection=JsonSerializer.Deserialize<JsonElement>(selected),
            analysisStatus=input.Analysis?.Status,
            verificationTables=input.Analysis?.Results?.VerificationEvidence?.Tables.Select(t=>new{t.Key,t.Fields,Rows=t.Rows.Count}),
            protection=new{identityUnchanged,statusUnchanged,selectionUnchanged,fileUnchanged},
            extractionIssues=input.Analysis?.Results?.VerificationEvidence?.Issues},
            new JsonSerializerOptions{WriteIndented=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
        Assert.True(identityUnchanged);Assert.True(statusUnchanged);Assert.True(selectionUnchanged);Assert.True(fileUnchanged);
        Assert.DoesNotContain(report.Findings,f=>f.Status==CheckStatus.ERROR);
        if(report.Findings.Any(f=>f.CheckId=="STATE-ANALYSIS"&&f.Kind==FindingKind.OperationNotPerformed))
        {
            Assert.DoesNotContain(report.Findings,f=>f.Status==CheckStatus.PASS);
            Assert.All(report.Coverage,c=>Assert.False(string.IsNullOrWhiteSpace(c.DeferredReason)));
            return true;
        }
        var candidates=input.Validation.LoadCases?.Items.Where(l=>l.Type=="LinearStatic"&&l.Loads is {Count:1}&&l.Loads[0].Type=="Load"&&
            input.Validation.LoadPatterns?.Items.Any(p=>p.Name==l.Loads[0].Name&&p.Type is "Live" or "SuperDead"&&p.SelfWeightMultiplier==0)==true&&
            input.Analysis!.Status?.Cases.Items.Any(s=>s.Name==l.Name&&s.State==AnalysisCaseState.Finished)==true&&
            input.Analysis.Results!.BaseReactions.Items.Any(r=>r.Case==l.Name&&r.StepType is "" or "Single Value"))??[];
        foreach(var candidate in candidates)
            Assert.Contains(report.Findings,f=>f.CheckId=="ANA-101"&&f.ElementId==candidate.Name&&f.CalculationValidation==CalculationValidation.IndependentLoadBalance);
        if(!candidates.Any())Assert.Contains(report.Findings,f=>f.CheckId=="ANA-101"&&f.Status is CheckStatus.NOT_VERIFIED or CheckStatus.NOT_APPLICABLE);
        if(input.Analysis!.Results?.VerificationEvidence?.Tables.Any(t=>t.Key=="Modal Periods And Frequencies"&&t.Rows.Count>0)==true)
            Assert.Contains(report.Findings,f=>f.CheckId=="ANA-106"&&f.CalculationValidation==CalculationValidation.ResultConsistency);
        Assert.Contains(report.Findings,f=>f.CheckId=="ANA-106"&&f.Status==CheckStatus.NOT_VERIFIED);
        return true;

        string Selection()
        {
            var selectedCases=new SortedDictionary<string,bool>(StringComparer.Ordinal);
            foreach(var item in session.ReadAnalysisStatus().Cases.Items)
            {bool flag=false;Assert.Equal(0,session.ModelForVerification.Results.Setup.GetCaseSelectedForOutput(item.Name,ref flag));selectedCases.Add("case:"+item.Name,flag);}
            int count=0;string[] names=[];
            Assert.Equal(0,session.ModelForVerification.RespCombo.GetNameList(ref count,ref names));
            foreach(var name in names)
            {bool flag=false;Assert.Equal(0,session.ModelForVerification.Results.Setup.GetComboSelectedForOutput(name,ref flag));selectedCases.Add("combo:"+name,flag);}
            return JsonSerializer.Serialize(selectedCases);
        }
    });

    [LiveEtabsFact]
    public Task DiscoverReadOnlyVerificationInputs() => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        int n = 0; string[] keys = [], names = []; int[] types = []; bool[] empty = [];
        Assert.Equal(0, session.ModelForVerification.DatabaseTables.GetAllTables(ref n, ref keys, ref names, ref types, ref empty));
        var inventory = Enumerable.Range(0, n).Select(i => new { Key = keys[i], Empty = empty[i] }).ToArray();
        var tables = new List<DatabaseTable>();
        foreach (var item in inventory.Where(i => !i.Empty && (i.Key.Contains("Load Assignments", StringComparison.OrdinalIgnoreCase) || i.Key.Contains("Loads Assignments", StringComparison.OrdinalIgnoreCase) ||
            i.Key.Contains("Mass", StringComparison.OrdinalIgnoreCase) || i.Key is "Joint Reactions" or "Joint Drifts" or "Joint Displacements" or "Modal Periods And Frequencies")))
        {
            try { tables.Add(session.ReadTable(item.Key)); } catch (ApiException) { }
        }
        var path = Path.Combine(AppContext.BaseDirectory, "verification-input-inventory.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { inventory, tables = tables.Select(t => new { t.Key, t.Fields, Count = t.Rows.Count, Sample = t.Rows.Take(3) }) }, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    });
}
