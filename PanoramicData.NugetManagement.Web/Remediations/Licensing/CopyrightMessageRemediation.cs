namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Adds Copyright message to Directory.Build.props.</summary>
public sealed class CopyrightMessageRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "LIC-03";
}
