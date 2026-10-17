using System.Xml.Linq;

public sealed class SvgPlanTests
{
    private static readonly XNamespace Svg="http://www.w3.org/2000/svg";
    private static InspectionReport Report(InspectionContext c,params Observation[] observations)
    {
        var rule=Rule.Create("GEO-999","Plan fixture","Synthetic object findings",[],_=>observations.Length==0?[Rule.Pass("No direct object finding")]:observations);
        return new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,"all",[rule],[rule.Definition]);
    }
    private static void WithDirectory(Action<string> test)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"svg-test-"+Guid.NewGuid().ToString("N"));
        try{test(directory);}
        finally{if(Directory.Exists(directory)){foreach(var file in Directory.EnumerateFiles(directory))File.Delete(file);Directory.Delete(directory);}}
    }
    [Fact]
    public void PlanProjectsGeometryWithPaddingAndHighestTypedSeverity()
    {
        var c=Context().Model(m=>m with {Areas=Data(m.Areas!.Items[0] with {Id="F1"})});
        var report=Report(c,new(CheckStatus.PASS,"pass","F1","Frame"),new(CheckStatus.WARNING,"warning","F1","Frame"),
            new(CheckStatus.FAIL,"fail","Frame:F1","Frame"),new(CheckStatus.WARNING,"area warning","Area:F1","Area"),
            new(CheckStatus.WARNING,"joint pair","A|B","JointPair"));
        WithDirectory(directory=>{
            SvgPlanGenerator.GeneratePlans(report,directory);
            var document=XDocument.Load(Path.Combine(directory,"L1.svg"));
            Assert.Equal("-300 -3300 3600 3600",document.Root!.Attribute("viewBox")!.Value);
            var line=Assert.Single(document.Descendants(Svg+"line"));
            Assert.Equal("#dc2626",line.Attribute("stroke")!.Value);
            Assert.Equal("0",line.Attribute("y1")!.Value);
            var area=Assert.Single(document.Descendants(Svg+"polygon"));
            Assert.Equal("#f59e0b",area.Attribute("stroke")!.Value);
            Assert.Contains("3000,-3000",area.Attribute("points")!.Value);
            var joint=document.Descendants(Svg+"circle").Single(e=>e.Attribute("data-element-id")!.Value=="A");
            Assert.Equal("#f59e0b",joint.Attribute("fill")!.Value);
            Assert.Equal(4,document.Descendants(Svg+"circle").Count());
        });
    }
    [Fact]
    public void StoriesUseSeparateFilesAndSafeCollisionResistantNames()
    {
        var c=Context().Model(m=>m with {Stories=new(0,[new("../escape",3000,3000,true,"None"),new("..\\escape",6000,3000,true,"None"),new("CON",9000,3000,true,"None")])});
        WithDirectory(directory=>{
            SvgPlanGenerator.GeneratePlans(Report(c),directory);
            var files=Directory.GetFiles(directory);
            Assert.Equal(3,files.Length);
            Assert.Equal(3,files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach(var file in files)
            {
                var root=XDocument.Load(file).Root!;
                var view=root.Attribute("viewBox")!.Value.Split(' ').Select(x=>double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
                Assert.True(view[2]>0&&view[3]>0);Assert.All(view,v=>Assert.True(double.IsFinite(v)));
                Assert.Equal(Path.GetFullPath(directory),Path.GetDirectoryName(Path.GetFullPath(file)));
            }
        });
    }
    [Fact]
    public void InvalidAreaIsNotDrawnAsPartialPolygonAndTextIsEscaped()
    {
        var c=Context().Model(m=>m with {Areas=Data(m.Areas!.Items[0] with {Joints=["A","B","missing"]}),
            Identity=m.Identity with {FilePath="<script>alert(1)</script>.edb"}});
        WithDirectory(directory=>{
            SvgPlanGenerator.GeneratePlans(Report(c),directory);
            var path=Path.Combine(directory,"L1.svg");var document=XDocument.Load(path);
            Assert.Empty(document.Descendants(Svg+"polygon"));
            Assert.Contains("1 areas",document.Descendants(Svg+"desc").Single().Value);
            Assert.DoesNotContain("<script>",File.ReadAllText(path));
            Assert.All(document.Descendants(Svg+"line"),e=>Assert.Equal("#d1d5db",e.Attribute("stroke")!.Value));
        });
    }
    [Fact]
    public void JointsAtOtherElevationsAreNotDrawnOnTheCurrentFloor()
    {
        var c=Context().Model(m=>m with {Joints=Data(m.Joints!.Items.Concat([new Joint("other",new(10,10,6000))]).ToArray())});
        WithDirectory(directory=>{
            SvgPlanGenerator.GeneratePlans(Report(c),directory);
            var document=XDocument.Load(Path.Combine(directory,"L1.svg"));
            Assert.DoesNotContain(document.Descendants(Svg+"circle"),e=>e.Attribute("data-element-id")!.Value=="other");
        });
    }
}
