namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>Adds Microsoft.NET.Test.Sdk package reference.</summary>
public sealed class TestSdkPresentRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TST-03";
}
