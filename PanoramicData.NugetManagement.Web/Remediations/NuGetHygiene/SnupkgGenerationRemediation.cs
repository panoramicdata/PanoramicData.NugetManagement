namespace PanoramicData.NugetManagement.Web.Remediations.NuGetHygiene;

/// <summary>Adds IncludeSymbols and SymbolPackageFormat for snupkg generation.</summary>
public sealed class SnupkgGenerationRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "PKG-01";
}
