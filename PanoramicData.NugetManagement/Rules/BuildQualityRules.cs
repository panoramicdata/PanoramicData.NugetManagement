using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that TreatWarningsAsErrors is enabled.
/// </summary>
public class TreatWarningsAsErrorsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-01";

	/// <inheritdoc />
	public override string RuleName => "TreatWarningsAsErrors enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"))
		{
			return Task.FromResult(Pass("TreatWarningsAsErrors is enabled in Directory.Build.props."));
		}

		return Task.FromResult(Fail(
			"TreatWarningsAsErrors is not enabled in Directory.Build.props.",
			"Add <TreatWarningsAsErrors>true</TreatWarningsAsErrors> to Directory.Build.props."));
	}
}

/// <summary>
/// Checks that nullable reference types are enabled.
/// </summary>
public class NullableEnabledRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-02";

	/// <inheritdoc />
	public override string RuleName => "Nullable enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<Nullable>enable</Nullable>"))
		{
			return Task.FromResult(Pass("Nullable is enabled in Directory.Build.props."));
		}

		return Task.FromResult(Fail(
			"Nullable reference types are not enabled in Directory.Build.props.",
			"Add <Nullable>enable</Nullable> to Directory.Build.props."));
	}
}

/// <summary>
/// Checks that ImplicitUsings is enabled.
/// </summary>
public class ImplicitUsingsRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "BLD-03";

	/// <inheritdoc />
	public override string RuleName => "ImplicitUsings enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.BuildQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj").ToList();
		var missing = new List<string>();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<ImplicitUsings>enable</ImplicitUsings>"))
			{
				missing.Add(csproj);
			}
		}

		return Task.FromResult(missing.Count == 0
			? Pass("All projects have ImplicitUsings enabled.")
			: Fail(
				$"The following projects do not have ImplicitUsings enabled: {string.Join(", ", missing)}",
				"Add <ImplicitUsings>enable</ImplicitUsings> to each .csproj."));
	}
}
