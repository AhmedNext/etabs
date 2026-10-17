using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Explicit result-only fallback. Never creates assignments, analysis status, or design results.</summary>
public sealed record ResultTableBundle(string ModelPath, string ModelSha256, DateTimeOffset CapturedAt,
    UnitContext Units, string Source, bool Complete, IReadOnlyList<AnalysisEvidenceTable> Tables);
public sealed record ResultCsvFile(string Key,string Path,string Sha256);
public sealed record ResultCsvManifest(string ModelPath,string ModelSha256,DateTimeOffset CapturedAt,UnitContext Units,string Source,bool Complete,IReadOnlyList<ResultCsvFile> Files);

public static class ResultTableRecovery
{
    public static InspectionInput Apply(InspectionInput input,ResultTableBundle bundle)
    {
        var tables=Validate(bundle,input.Model.Identity);
        if(input.Analysis?.Results?.VerificationEvidence is not {} evidence)
            throw new ConfigurationException("A model and table inventory must be read before result-table recovery.");
        var merged=evidence.Tables.ToList();
        foreach(var table in tables)
        {
            var previous=merged.SingleOrDefault(t=>t.Key==table.Key);
            if(previous is not null&&previous.UnavailableReason is null&&previous.Rows.Count>0)continue;
            merged.RemoveAll(t=>t.Key==table.Key);merged.Add(table);
        }
        return input with{Analysis=input.Analysis with{Results=input.Analysis.Results with{VerificationEvidence=evidence with{Tables=merged}}}};
    }
    public static readonly string[] SupportedTables = ["Base Reactions", "Joint Reactions", "Joint Displacements", "Story Forces", "Story Drifts", "Joint Drifts", "Modal Periods And Frequencies", "Modal Participating Mass Ratios", "Mass Summary by Group", "Mass Source Definition"];
    public static string HashModel(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public static ResultTableBundle Load(string path)
    {
        var json=File.ReadAllText(path);var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        using var document=JsonDocument.Parse(json);
        if(document.RootElement.EnumerateObject().Any(p=>p.Name.Equals("Files",StringComparison.OrdinalIgnoreCase)))
        {
            var manifest=JsonSerializer.Deserialize<ResultCsvManifest>(json,options)??throw new ConfigurationException("The result manifest is empty.");
            var tables=manifest.Files.Select(file=>
            {
                var csvPath=System.IO.Path.GetFullPath(file.Path,System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                if(!file.Sha256.Equals(HashModel(csvPath),StringComparison.OrdinalIgnoreCase))throw new ConfigurationException("Exported table file changed: "+file.Key);
                return ReadCsv(file.Key,File.ReadAllText(csvPath));
            }).ToArray();
            return new(manifest.ModelPath,manifest.ModelSha256,manifest.CapturedAt,manifest.Units,manifest.Source,manifest.Complete,tables);
        }
        return JsonSerializer.Deserialize<ResultTableBundle>(json,options)??throw new ConfigurationException("The result-table file is empty.");
    }
    public static AnalysisEvidenceTable ReadCsv(string key,string csv)
    {
        List<string[]> records=[];List<string> row=[];var cell=new System.Text.StringBuilder();bool quoted=false;
        for(int i=0;i<csv.Length;i++)
        {
            char c=csv[i];
            if(c=='"'){if(quoted&&i+1<csv.Length&&csv[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
            else if(!quoted&&(c==','||c=='\n'||c=='\r'))
            {
                row.Add(cell.ToString());cell.Clear();
                if(c!=','){if(c=='\r'&&i+1<csv.Length&&csv[i+1]=='\n')i++;if(row.Any(s=>!string.IsNullOrWhiteSpace(s)))records.Add(row.ToArray());row.Clear();}
            }
            else cell.Append(c);
        }
        if(quoted)throw new ConfigurationException("Unclosed quoted cell in exported table.");
        if(cell.Length>0||row.Count>0){row.Add(cell.ToString());records.Add(row.ToArray());}
        if(records.Count>0&&records[0][0].TrimStart('\uFEFF').StartsWith("TABLE:",StringComparison.OrdinalIgnoreCase))
        {
            if(records[0][0].TrimStart('\uFEFF')[6..].Trim()!=key)throw new ConfigurationException("CSV table title does not match the manifest.");records.RemoveAt(0);
        }
        if(records.Count<2)throw new ConfigurationException("Exported table has no data rows: "+key);
        string Field(string s)=>string.Concat(s.TrimStart('\uFEFF').Where(c=>!char.IsWhiteSpace(c)));
        var fields=records[0].Select(Field).ToArray();
        if(fields.Any(string.IsNullOrWhiteSpace)||fields.Distinct(StringComparer.Ordinal).Count()!=fields.Length)throw new ConfigurationException("Invalid or duplicate exported table headers.");
        return new(key,fields,records.Skip(1).Select(r=>
        {
            if(r.Length!=fields.Length)throw new ConfigurationException("Exported table contains incomplete rows: "+key);
            return (IReadOnlyDictionary<string,string>)fields.Select((f,i)=>(f,value:r[i])).ToDictionary(x=>x.f,x=>x.value,StringComparer.Ordinal);
        }).ToArray());
    }

    public static IReadOnlyList<AnalysisEvidenceTable> Validate(ResultTableBundle bundle, ModelIdentity model)
    {
        if(!model.IsLocked)throw new ConfigurationException("Result-table fallback requires a locked, analyzed model.");
        if(!Path.GetFullPath(bundle.ModelPath).Equals(Path.GetFullPath(model.FilePath),StringComparison.OrdinalIgnoreCase)||
            !string.Equals(bundle.ModelSha256,HashModel(model.FilePath),StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("Result-table fallback belongs to a different or changed model file.");
        if(!bundle.Complete||string.IsNullOrWhiteSpace(bundle.Source)||bundle.Tables.Count==0)
            throw new ConfigurationException("Result-table fallback needs a named source and complete tables.");
        if(bundle.CapturedAt> DateTimeOffset.UtcNow.AddMinutes(1)||bundle.CapturedAt < File.GetLastWriteTimeUtc(model.FilePath))
            throw new ConfigurationException("Result-table capture predates the saved model or has a future timestamp.");
        if(bundle.Tables.Select(t=>t.Key).Distinct(StringComparer.Ordinal).Count()!=bundle.Tables.Count)
            throw new ConfigurationException("Duplicate result tables in fallback file.");
        return bundle.Tables.Select(t=>Normalize(t,bundle.Units,model.DisplayUnits,bundle.Source,bundle.CapturedAt)).ToArray();
    }

    public static AnalysisEvidenceTable Normalize(AnalysisEvidenceTable table,UnitContext source,UnitContext target,string provenance,DateTimeOffset captured)
    {
        if(!SupportedTables.Contains(table.Key,StringComparer.Ordinal)||table.UnavailableReason is not null||table.Rows.Count==0)
            throw new ConfigurationException("Unsupported, empty, or unreadable fallback table: "+table.Key);
        if(table.Fields.Distinct(StringComparer.Ordinal).Count()!=table.Fields.Count)
            throw new ConfigurationException("Duplicate columns in "+table.Key);
        double force=Force(source.Force)/Force(target.Force),length=Length(source.Length)/Length(target.Length);
        var rows=table.Rows.Select(row=>
        {
            if(table.Fields.Any(k=>!row.ContainsKey(k)))throw new ConfigurationException("Incomplete row in "+table.Key);
            var output=new Dictionary<string,string>(row,StringComparer.Ordinal);
            foreach(var key in table.Fields)
            {
                double? scale=key switch{
                    "FX" or "FY" or "FZ" or "VX" or "VY" or "P"=>force,
                    "MX" or "MY" or "MZ" or "T"=>force*length,
                    "X" or "Y" or "Z" or "Ux" or "Uy" or "Uz" or "DisplacementX" or "DisplacementY"=>length,
                    "MassX" or "MassY" or "MassZ"=>force/length,
                    "Drift" or "Period" or "Frequency" or "CircFreq" or "Eigenvalue"=>1,
                    _=>null};
                if(scale is null)continue;
                var value=Parse(row[key]);var error=DisplayError(row[key]);
                output[key]=(value*scale.Value).ToString("G17",CultureInfo.InvariantCulture);
                output["__uncertainty_"+key]=(error*Math.Abs(scale.Value)).ToString("G17",CultureInfo.InvariantCulture);
            }
            return (IReadOnlyDictionary<string,string>)output;
        }).ToArray();
        return table with{Rows=rows,RecoverySource=provenance,CapturedAt=captured};
    }
    public static double Parse(string text)=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:
        throw new ConfigurationException("Invalid numeric value in recovered result table: "+text);
    public static double DisplayError(string text)
    {
        var parts=text.Trim().ToLowerInvariant().Split('e');var decimals=parts[0].Contains('.')?parts[0].Length-parts[0].IndexOf('.')-1:0;
        var exponent=parts.Length==2?int.Parse(parts[1],CultureInfo.InvariantCulture):0;
        var error=.5*Math.Pow(10,exponent-decimals);
        if(!double.IsFinite(error))throw new ConfigurationException("Invalid displayed precision.");return error;
    }
    private static double Force(string unit)=>unit switch{"N"=>1,"kN"=>1000,"kip"=>4448.2216152605,"lb"=>4.4482216152605,"kgf"=>9.80665,"tonf"=>9806.65,_=>throw new ConfigurationException("Unsupported fallback force unit: "+unit)};
    private static double Length(string unit)=>unit switch{"mm"=>.001,"cm"=>.01,"m"=>1,"in" or "inch"=>.0254,"ft"=>.3048,_=>throw new ConfigurationException("Unsupported fallback length unit: "+unit)};
}
