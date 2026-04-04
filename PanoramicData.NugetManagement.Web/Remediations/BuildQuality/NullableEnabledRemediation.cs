namespace PanoramicData.NugetManagement.Web.Remediations.BuildQuality;

/// <summary>Adds Nullable enable to Directory.Build.props.</summary>
public sealed class NullableEnabledRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "BLD-02";
}
