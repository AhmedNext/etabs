using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;

/// <summary>Reads an already-open ETABS report grid. No clicks, scrolling, selection, or model writes.</summary>
public static class OpenResultTableReader
{
    public static IReadOnlyList<AnalysisEvidenceTable> Read(ModelIdentity model)
    {
        if(!model.IsLocked)return [];
        var name=Path.GetFileNameWithoutExtension(model.FilePath);
        var matches=Process.GetProcessesByName("ETABS").Where(p=>p.MainWindowHandle!=IntPtr.Zero&&p.MainWindowTitle.Contains(name,StringComparison.Ordinal)).ToArray();
        if(matches.Length!=1)return [];
        var hash=ResultTableRecovery.HashModel(model.FilePath);
        var root=AutomationElement.FromHandle(matches[0].MainWindowHandle);
        var dialogs=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"fReportDB"));
        List<AnalysisEvidenceTable> output=[];
        foreach(AutomationElement dialog in dialogs)
        {
            var key=dialog.Current.Name;
            if(!ResultTableRecovery.SupportedTables.Contains(key,StringComparer.Ordinal))continue;
            var grid=dialog.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"datGrid"));
            if(grid is null)continue;
            var nodes=grid.FindAll(TreeScope.Children,Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            var top=nodes.SingleOrDefault(e=>e.Current.Name=="Top Row");
            if(top is null)continue;
            var headers=top.FindAll(TreeScope.Children,Condition.TrueCondition).Cast<AutomationElement>().Where(e=>e.Current.Name!="Top Left Header Cell").Select(e=>e.Current.Name).ToArray();
            var fields=headers.Select(Field).ToArray();
            if(fields.Any(string.IsNullOrWhiteSpace)||fields.Distinct(StringComparer.Ordinal).Count()!=fields.Length)continue;
            var rowElements=nodes.Where(e=>Regex.IsMatch(e.Current.Name,@"^Row \d+$",RegexOptions.CultureInvariant)).OrderBy(e=>int.Parse(e.Current.Name[4..],CultureInfo.InvariantCulture)).ToArray();
            var countLabels=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>()
                .Select(e=>Regex.Match(e.Current.Name,@"^of\s+(\d+)$",RegexOptions.CultureInvariant)).Where(m=>m.Success).ToArray();
            if(countLabels.Length!=1||int.Parse(countLabels[0].Groups[1].Value,CultureInfo.InvariantCulture)!=rowElements.Length||rowElements.Length==0)
                continue; // A visible page or virtualized subset is not a complete table.
            List<IReadOnlyDictionary<string,string>> rows=[];
            for(int i=0;i<rowElements.Length;i++)
            {
                if(rowElements[i].Current.Name!="Row "+i)throw new ConfigurationException("Open ETABS table has noncontiguous rows; export the complete table.");
                var cells=rowElements[i].FindAll(TreeScope.Children,Condition.TrueCondition).Cast<AutomationElement>().Where(e=>e.Current.ControlType!=ControlType.Header).ToArray();
                if(cells.Length!=fields.Length)throw new ConfigurationException("Open ETABS table has hidden or incomplete cells; export all columns.");
                rows.Add(fields.Select((f,j)=>(f,value:Value(cells[j]))).ToDictionary(x=>x.f,x=>x.value,StringComparer.Ordinal));
            }
            var source=ReadUnits(headers,fields,model.DisplayUnits);
            output.Add(ResultTableRecovery.Normalize(new(key,fields,rows),source,model.DisplayUnits,
                "Read-only Windows accessibility: ETABS open table; displayed precision; complete "+rows.Count+" rows",DateTimeOffset.UtcNow));
        }
        if(hash!=ResultTableRecovery.HashModel(model.FilePath))throw new ModelChangedException("Model file changed while reading the open result table.");
        return output;
    }
    private static string Value(AutomationElement e)
    {
        if(e.TryGetCurrentPattern(ValuePattern.Pattern,out var p))return ((ValuePattern)p).Current.Value;
        throw new ConfigurationException("A result cell does not expose a readable value.");
    }
    private static string Field(string name)
    {
        var clean=Regex.Replace(name,@"\s+"," ").Trim();
        foreach(var field in new[]{"Output Case","Case Type","Unique Name","Step Type","Step Number","Step Label","Displacement X","Displacement Y","Mass X","Mass Y","Mass Z","Sum UX","Sum UY","Sum UZ","Sum RX","Sum RY","Sum RZ","Story","Label","Location","Mode","Period","Frequency","CircFreq","Eigenvalue","Drift","Direction","Group","Name","IncLateral","IncVertical","FX","FY","FZ","MX","MY","MZ","VX","VY","Ux","Uy","Uz","Rx","Ry","Rz","P","T","X","Y","Z"})
            if(clean==field||clean.StartsWith(field+" ",StringComparison.Ordinal))return field.Replace(" ","");
        return "";
    }
    private static UnitContext ReadUnits(string[] headers,string[] fields,UnitContext defaults)
    {
        string? force=null,length=null;
        for(int i=0;i<fields.Length;i++)
        {
            var tokens=Regex.Split(headers[i].Trim(),@"\s+");
            if(fields[i] is "FX" or "FY" or "FZ" or "P" or "VX" or "VY")
            {if(tokens.Length<2)throw new ConfigurationException("Missing force units in open result table.");if(force is not null&&force!=tokens[^1])throw new ConfigurationException("Mixed force units in open table.");force=tokens[^1];}
            if(fields[i] is "X" or "Y" or "Z" or "Ux" or "Uy" or "Uz" or "DisplacementX" or "DisplacementY")
            {if(tokens.Length<2)throw new ConfigurationException("Missing length units in open result table.");if(length is not null&&length!=tokens[^1])throw new ConfigurationException("Mixed length units in open table.");length=tokens[^1];}
        }
        foreach(var i in Enumerable.Range(0,fields.Length).Where(i=>fields[i] is "MX" or "MY" or "MZ" or "T"))
        {
            var unit=Regex.Split(headers[i].Trim(),@"\s+")[^1];var parts=unit.Split('-');
            if(parts.Length!=2)throw new ConfigurationException("Missing moment units in open result table.");
            if(force is not null&&force!=parts[0]||length is not null&&length!=parts[1])throw new ConfigurationException("Inconsistent moment units in open table.");
            force=parts[0];length=parts[1];
        }
        if(fields.Any(f=>f is "MassX" or "MassY" or "MassZ"))throw new ConfigurationException("Mass table screen units require an explicit exported result bundle.");
        return new(force??defaults.Force,length??defaults.Length,defaults.Temperature);
    }
}
