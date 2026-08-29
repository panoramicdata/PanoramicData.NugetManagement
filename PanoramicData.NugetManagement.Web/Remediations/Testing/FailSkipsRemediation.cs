namespace PanoramicData.NugetManagement.Web.Remediations.Testing;

/// <summary>Writes xunit.runner.json with failSkips: true for each test project missing it.</summary>
public sealed class FailSkipsRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "TST-05";
}
