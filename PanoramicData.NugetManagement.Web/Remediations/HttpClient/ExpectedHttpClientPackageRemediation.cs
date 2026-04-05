namespace PanoramicData.NugetManagement.Web.Remediations.HttpClient;

/// <summary>Adds the expected HTTP client package to Directory.Packages.props.</summary>
public sealed class ExpectedHttpClientPackageRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "HTTP-01";
}
