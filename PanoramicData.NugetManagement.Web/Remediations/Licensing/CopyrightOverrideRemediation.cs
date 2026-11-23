namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Removes Copyright from project files so the Directory.Build.props value applies.</summary>
public sealed class CopyrightOverrideRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "LIC-05";
}
