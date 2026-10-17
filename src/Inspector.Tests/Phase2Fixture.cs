
internal static class Phase2Fixture
{
    public static DataSet<T> Data<T>(params T[] items) => new(items, [], []);
    public static InspectionContext Context()
    {
        var units = new UnitContext("N", "mm", "C");
        var joints = Data(new Joint("A", new(0, 0, 3000)), new Joint("B", new(3000, 0, 3000)),
            new Joint("C", new(3000, 3000, 3000)), new Joint("D", new(0, 3000, 3000)));
        var model = new ModelData(new("22.7.0", "reference.edb", true, units, units, DateTimeOffset.UnixEpoch),
            new(0, [new("L1", 3000, 3000, true, "None")]), joints,
            Data(new Frame("F1", "L1", "B", "A", "B", joints.Items[0].Position, joints.Items[1].Position, 0)),
            Data(new Area("S1", "S1", "L1", "S", "Floor", false, ["A", "B", "C", "D"])),
            Data(new Material("C25", "Concrete", 0, 0.000024, 2.446e-9, 25000, 0.2, 25, null, null)),
            Data(new FrameSection("B", "Rectangular", "C25", 600, 300, null, null)),
            Data(new AreaSection("S", "Slab", "ShellThin", "C25", 200, null)), [], true, TimeSpan.Zero);
        var validation = new ValidationData(
            Data(joints.Items.Select(j => new JointAssignment(j.Id, new bool[6], "FromShellObject", null)).ToArray()),
            Data(new FrameAssignment("F1", "Straight", null, new bool[6], new bool[6], new double[6], new double[6])),
            Data(new AreaAssignment("S1", "D1", null)), Data<LinkConnection>(),
            Data(new LoadPattern("D", "Dead", 1), new LoadPattern("L", "Live", 0)),
            Data(new LoadCase("Dcase", "LinearStatic", 0, [new("Load", "D", 1)]), new LoadCase("Lcase", "LinearStatic", 0, [new("Load", "L", 1)])),
            Data(new LoadCombination("ULS", 0, [new("LoadCase", "Dcase", 0, 1.2), new("LoadCase", "Lcase", 0, 1.6)])),
            Data(new MassSource("M", true, false, true, true, [new("D", 1), new("L", 0.25)])),
            Data(new Diaphragm("D1", false)), []);
        var settings = new InspectionSettings("1.0", true,
            new(new(1, "mm"), new(5, "mm"), [], [], true),
            new([new("D", "Dead", 1), new("L", "Live", 0)], ["ULS"]));
        return new(new(model, validation), new(settings, "reference-project.json", "reference-hash", true), DateTimeOffset.UnixEpoch);
    }
    public static InspectionContext Model(this InspectionContext c, Func<ModelData, ModelData> change) => c with { Input = c.Input with { Model = change(c.Input.Model) } };
    public static InspectionContext Validation(this InspectionContext c, Func<ValidationData, ValidationData> change) => c with { Input = c.Input with { Validation = change(c.Input.Validation) } };
    public static InspectionContext Settings(this InspectionContext c, Func<InspectionSettings, InspectionSettings> change) => c with { Configuration = c.Configuration with { Settings = change(c.Configuration.Settings) } };
}


