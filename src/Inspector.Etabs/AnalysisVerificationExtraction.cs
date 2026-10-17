public sealed partial class EtabsSession
{
    private AnalysisVerificationEvidence ReadVerificationEvidence()
    {
        List<ExtractionIssue> issues = []; List<AnalysisEvidenceTable> tables = [];
        List<AnalysisTableSnapshot> inventory = [];
        // Table-display selection is separate from Results.Setup flags. Check its own
        // getters; flags alone cannot establish that the display selection is readable.
        try
        {
            int n=0;string[] names=[];
            guard.Call("DatabaseTables.GetLoadCasesSelectedForDisplay",()=>tableApi.GetLoadCasesSelectedForDisplay(ref n,ref names));
            ApiGuard.Arrays("GetLoadCasesSelectedForDisplay",n,names);
        }
        catch(ApiException ex){issues.Add(new("Table display case selection",ex.Message+". In ETABS, open Display > Show Tables > Select Load Cases and check that the desired result cases are selected. Table-display selection is separate from Results.Setup flags. The inspector leaves both selections unchanged.",ex.Operation,ex.ReturnCode));}
        try
        {
            int n=0;string[] names=[];
            guard.Call("DatabaseTables.GetLoadCombinationsSelectedForDisplay",()=>tableApi.GetLoadCombinationsSelectedForDisplay(ref n,ref names));
            ApiGuard.Arrays("GetLoadCombinationsSelectedForDisplay",n,names);
        }
        catch(ApiException ex){issues.Add(new("Table display combination selection",ex.Message,ex.Operation,ex.ReturnCode));}
        try
        {
            int n = 0; string[] keys = [], names = []; int[] types = []; bool[] empty = [];
            guard.Call("DatabaseTables.GetAllTables:Verification", () => tableApi.GetAllTables(ref n, ref keys, ref names, ref types, ref empty));
            ApiGuard.Arrays("GetAllTables:Verification", n, keys, names, types, empty);
            inventory.AddRange(Enumerable.Range(0, n).Select(i => new AnalysisTableSnapshot(keys[i], names[i], empty[i], [], 0)));
        }
        catch (ApiException ex) { issues.Add(new("VerificationInventory", ex.Message, ex.Operation, ex.ReturnCode)); }
        var wanted = inventory.Where(t => t.Key.Contains("Load Assignments", StringComparison.Ordinal) || t.Key.Contains("Loads Assignments", StringComparison.Ordinal) ||
            t.Key.EndsWith("Assignments - Additional Mass", StringComparison.Ordinal) || t.Key is "Base Reactions" or "Joint Reactions" or "Joint Displacements" or "Story Forces" or "Story Drifts" or "Joint Drifts" or "Mass Source Definition" or "Mass Summary by Group" or "Modal Periods And Frequencies")
            .Select(t => t.Key).Distinct(StringComparer.Ordinal);
        foreach (var key in wanted)
        {
            if (inventory.Single(t => t.Key == key).IsEmpty) { tables.Add(new(key, [], [])); continue; }
            try { var table = ReadTable(key); tables.Add(new(key, table.Fields, table.Rows)); }
            catch (ApiException ex)
            {
                tables.Add(new(key, [], [], ex.Message));
                issues.Add(new(key, ex.Message, ex.Operation, ex.ReturnCode));
            }
        }
        List<GravityLoadAssignment> loads=[];bool loadRead=true;
        try
        {
            if(inventory.Any(t=>t.Key=="Area Load Assignments - Uniform"&&!t.IsEmpty))
            {
                int n=0;string[] objects=[],patterns=[],systems=[];int[] directions=[];double[] values=[];
                guard.Call("AreaObj.GetLoadUniform:Verification",()=>areaApi.GetLoadUniform("All",ref n,ref objects,ref patterns,ref systems,ref directions,ref values,eItemType.Group));
                ApiGuard.Arrays("GetLoadUniform",n,objects,patterns,systems,directions,values);
                loads.AddRange(Enumerable.Range(0,n).Select(i=>new GravityLoadAssignment("Area",objects[i],patterns[i],1,systems[i],directions[i],0,0,ApiGuard.Finite("GetLoadUniform",values[i]),ApiGuard.Finite("GetLoadUniform",values[i]))));
            }
            if(inventory.Any(t=>t.Key=="Frame Loads Assignments - Distributed"&&!t.IsEmpty))
            {
                int n=0;string[] objects=[],patterns=[],systems=[];int[] types=[],directions=[];double[] r1=[],r2=[],a=[],b=[],v1=[],v2=[];
                guard.Call("FrameObj.GetLoadDistributed:Verification",()=>frameApi.GetLoadDistributed("All",ref n,ref objects,ref patterns,ref types,ref systems,ref directions,ref r1,ref r2,ref a,ref b,ref v1,ref v2,eItemType.Group));
                ApiGuard.Arrays("GetLoadDistributed",n,objects,patterns,types,systems,directions,r1,r2,a,b,v1,v2);
                loads.AddRange(Enumerable.Range(0,n).Select(i=>new GravityLoadAssignment("Frame",objects[i],patterns[i],types[i],systems[i],directions[i],ApiGuard.Finite("GetLoadDistributed",a[i]),ApiGuard.Finite("GetLoadDistributed",b[i]),ApiGuard.Finite("GetLoadDistributed",v1[i]),ApiGuard.Finite("GetLoadDistributed",v2[i]))));
            }
        }
        catch(ApiException ex){loadRead=false;issues.Add(new("GravityAssignments",ex.Message,ex.Operation,ex.ReturnCode));}
        List<string> geometryLimitations=[];
        try
        {
            int n=0;string[] frames=[];guard.Call("FrameObj.GetNameList:WeightGeometry",()=>frameApi.GetNameList(ref n,ref frames));
            ApiGuard.Arrays("FrameObj.GetNameList:WeightGeometry",n,frames);
            foreach(var name in frames)
            {
                bool auto=false;double i=0,j=0,rigid=0;
                guard.Call("FrameObj.GetEndLengthOffset:"+name,()=>frameApi.GetEndLengthOffset(name,ref auto,ref i,ref j,ref rigid));
                if(auto||i!=0||j!=0)geometryLimitations.Add("Automatic/nonzero end offsets need an independently validated overlap-aware self-weight treatment.");
                int cardinal=0;bool m2=false,m3=false;double[] oi=[],oj=[];string system="";
                guard.Call("FrameObj.GetInsertionPoint:"+name,()=>frameApi.GetInsertionPoint(name,ref cardinal,ref m2,ref m3,ref oi,ref oj,ref system));
                ApiGuard.Arrays("GetInsertionPoint",3,oi,oj);
                if(oi.Concat(oj).Any(x=>x!=0))geometryLimitations.Add("Nonzero joint insertion offsets need an independent physical-length calculation.");
            }
        }
        catch(ApiException ex){geometryLimitations.Add(ex.Message);}
        if(tables.Any(t=>t.UnavailableReason is not null))
        {
            try
            {
                foreach(var recovered in OpenResultTableReader.Read(ReadIdentity()))
                {
                    var old=tables.SingleOrDefault(t=>t.Key==recovered.Key);
                    if(old is null||old.UnavailableReason is not null||old.Rows.Count==0)
                    {tables.RemoveAll(t=>t.Key==recovered.Key);tables.Add(recovered);}
                }
            }
            catch(Exception ex) when(ex is ConfigurationException or System.Windows.Automation.ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.Windows.Automation.ElementNotEnabledException)
            {issues.Add(new("Open result table fallback",ex.Message,"Read-only Windows accessibility",null));}
        }
        return new(inventory, tables, inventory.Count > 0, issues)
        {
            GravityLoads=new(loads,[new("AreaObj.GetLoadUniform + FrameObj.GetLoadDistributed",DateTimeOffset.UtcNow,units)],[]){ReadSucceeded=loadRead},
            SimpleFrameWeightGeometryVerified=geometryLimitations.Count==0,
            GeometryLimitations=geometryLimitations.Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
