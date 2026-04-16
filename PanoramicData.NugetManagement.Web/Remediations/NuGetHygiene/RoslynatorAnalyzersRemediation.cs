namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Removes Roslynator.Analyzers PackageReference from all project files.</summary>
public sealed class RoslynatorAnalyzersRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-08";
}
