namespace PanoramicData.NugetManagement.Web.Remediations.Versioning;

/// <summary>Deletes a committed dotnet tool manifest that only ever pinned the nbgv CLI.</summary>
public sealed class NbgvToolManifestRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "VER-04";
}
