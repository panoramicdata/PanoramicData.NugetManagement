namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>Declares the Microsoft.Testing.Platform test runner in global.json.</summary>
public sealed class MtpTestRunnerRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TST-06";
}
