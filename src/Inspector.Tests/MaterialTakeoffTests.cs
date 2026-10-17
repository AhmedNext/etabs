public sealed class MaterialTakeoffTests
{
    private static DatabaseTable Table(string key,params (string Field,string Value)[] fields)=>
        DatabaseTable.Parse(key,1,fields.Select(f=>f.Field).ToArray(),1,fields.Select(f=>f.Value).ToArray());
    [Fact]
    public void ExplicitWeightAndVolumeAreParsedWithoutConversions()
    {
        var table=Table("Material List 1 - By Object Type",("ObjectType","Column"),("Material","C35"),("TotalWeight","1612800"),("TotalVolume","67200000000"));
        var row=Assert.Single(MaterialTakeoffParser.Parse(table));
        Assert.Equal("Column",row.ObjectType);Assert.Equal("C35",row.Material);
        Assert.Equal(1612800,row.TotalWeight);Assert.Equal(67200000000,row.TotalVolume);
    }
    [Fact]
    public void InstalledTableVolumeUsesTheMatchingMaterialUnitWeight()
    {
        var table=Table("Material List by Object Type",("ObjectType","Column"),("Material","C35"),("Weight","1612800"));
        Material material=new("C35","Concrete",0,.000024,0,25000,.2,35,null,null);
        var row=Assert.Single(MaterialTakeoffParser.Parse(table,[material]));
        Assert.Equal(67.2,row.TotalVolume/1e9,10);
        Assert.Throws<ApiException>(()=>MaterialTakeoffParser.Parse(table));
        Assert.Throws<ApiException>(()=>MaterialTakeoffParser.Parse(table,[material with {UnitWeight=0}]));
        Assert.Throws<ApiException>(()=>MaterialTakeoffParser.Parse(table,[material,material]));
    }
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("not a number")]
    [InlineData("")]
    public void MalformedAndNonphysicalQuantitiesFailExplicitly(string value)
    {
        var table=Table("Material List 1 - By Object Type",("ObjectType","Column"),("Material","C35"),("TotalWeight",value),("TotalVolume","1"));
        Assert.Throws<ApiException>(()=>MaterialTakeoffParser.Parse(table));
    }
    [Fact]
    public void RequestedTableMustHaveAllRequiredFields()
    {
        var table=Table("Material List 1 - By Object Type",("ObjectType","Column"),("Material","C35"),("TotalWeight","1"));
        Assert.Throws<ApiException>(()=>MaterialTakeoffParser.Parse(table));
    }
}
