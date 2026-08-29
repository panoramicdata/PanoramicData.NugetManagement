namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>Replaces FluentAssertions with AwesomeAssertions across manifests and sources.</summary>
public sealed class AwesomeAssertionsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TST-08";
}
