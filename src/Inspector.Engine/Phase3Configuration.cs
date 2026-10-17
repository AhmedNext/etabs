
public static partial class ProjectConfiguration
{
    private static void ValidatePhase3(Phase3ReviewSettings? r)
    {
        if(r is null)return;
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,string text){if(!condition)throw new ConfigurationException("phase3Review: "+text);}
        bool Positive(double value)=>double.IsFinite(value)&&value>0;
        bool Ratio(double value)=>double.IsFinite(value)&&value>=0;
        if(r.ReferencesConfirmed)Require(!string.IsNullOrWhiteSpace(r.ReferenceSource),"confirmed references require a source.");
        foreach(var value in new[]{r.MaximumJointDisplacement,r.MemberForceOutlierRatio,r.MaximumFrameForceJump,r.MaximumDiaphragmFlexibilityRatio})
            if(value is {} v)Require(Positive(v),"limits must be finite and positive.");
        if(r.MinimumModalParticipation is {} mass)Require(Positive(mass)&&mass<=1,"minimum modal participation must be in (0,1].");
        if(r.DiaphragmFlexibilityRatio is {} flexibility)Require(Ratio(flexibility),"diaphragm ratio must be finite and nonnegative.");
        Require(r.DisplacementCases is not null&&r.DisplacementDirections is not null&&r.RequiredSeismicCombinations is not null,"remaining analysis-review lists cannot be null.");
        Require(r.DisplacementCases!.All(x=>!string.IsNullOrWhiteSpace(x))&&r.DisplacementCases.Distinct().Count()==r.DisplacementCases.Count,"displacement cases must be nonempty and unique.");
        Require(r.DisplacementDirections!.All(x=>x is "X" or "Y" or "Z")&&r.DisplacementDirections.Distinct().Count()==r.DisplacementDirections.Count,"displacement directions must be unique X/Y/Z.");
        Require(r.RequiredSeismicCombinations!.All(x=>x is not null&&!string.IsNullOrWhiteSpace(x.Name)&&x.Terms is {Count:>0}&&x.Terms.All(t=>t is not null&&t.Type=="LoadCase"&&!string.IsNullOrWhiteSpace(t.Name)&&double.IsFinite(t.Factor)))&&r.RequiredSeismicCombinations.Select(x=>x.Name).Distinct().Count()==r.RequiredSeismicCombinations.Count,"seismic combination templates require unique names and finite LoadCase terms.");
        if(r.ModalCase is not null)Require(!string.IsNullOrWhiteSpace(r.ModalCase),"modal case cannot be blank.");
        if(r.ExpectedConcreteCode is not null)Require(!string.IsNullOrWhiteSpace(r.ExpectedConcreteCode),"expected concrete code cannot be blank.");
    }
}


