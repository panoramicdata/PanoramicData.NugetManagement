using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that the repository can show what Codacy would report at build time: <c>dotnet build -p:CodacyParity=true</c>.
/// </summary>
/// <remarks>
/// Codacy's engines otherwise run only after a push, so each finding costs a CI round trip. The parity build is
/// opt-in and warnings only, so normal builds and CI are unchanged. A repository that already has its own
/// Directory.Build.targets is reported without a fix, because overwriting it would discard what is there.
/// </remarks>
public class CodacyParityBuildRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CQ-10";

	/// <inheritdoc />
	public override string RuleName => "Build can show Codacy findings (opt-in parity build)";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CodeQuality;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.FindFiles(".csproj").Any())
		{
			return Task.FromResult(NotApplicable("No C# project, so Codacy's SonarC# and Lizard have nothing to report."));
		}

		var targets = context.GetFileContent(CodacyParityStandards.TargetsPath);
		if (targets is not null && !Contains(targets, CodacyParityStandards.Marker))
		{
			return Task.FromResult(Fail(
				"Directory.Build.targets exists without the Codacy parity build.",
				new RuleAdvisory
				{
					Summary = "Add the Codacy parity targets to the existing Directory.Build.targets.",
					Detail = "The repository's own `Directory.Build.targets` is not replaced. Merge in the targets from the standard (guarded by `-p:CodacyParity=true`, warnings only), and add `build/codacy-parity.globalconfig` and `build/CodeMetricsConfig.txt`."
				}));
		}

		var missing = MissingFiles(context, targets is not null);
		if (missing.Count == 0)
		{
			return Task.FromResult(Pass("The opt-in Codacy parity build is in place."));
		}

		return Task.FromResult(Fail(
			"The opt-in Codacy parity build is missing: " + string.Join(", ", missing.Select(m => m.Path)) + ".",
			new RuleAdvisory
			{
				Summary = "Add the opt-in Codacy parity build.",
				Detail = "Adds `Directory.Build.targets`, `build/codacy-parity.globalconfig` and `build/CodeMetricsConfig.txt`. `dotnet build -p:CodacyParity=true` then reports as warnings what Codacy's SonarC# and Lizard would flag; normal builds and CI are unchanged. Folders that `.codacy.yml` excludes (for example EF migrations) need their own `.editorconfig` with the same rules set to none.",
				Data = new()
				{
					["remediation_type"] = "create_files",
					["paths"] = missing.Select(m => m.Path).ToArray(),
					["contents"] = missing.Select(m => m.Content).ToArray()
				}
			}));
	}

	private static List<(string Path, string Content)> MissingFiles(RepositoryContext context, bool hasTargets)
	{
		var wanted = new List<(string Path, string Content)>();
		if (!hasTargets)
		{
			wanted.Add((CodacyParityStandards.TargetsPath, CodacyParityStandards.TargetsContent));
		}

		wanted.Add((CodacyParityStandards.GlobalConfigPath, CodacyParityStandards.GlobalConfigContent));
		wanted.Add((CodacyParityStandards.CodeMetricsPath, CodacyParityStandards.CodeMetricsContent));
		return [.. wanted.Where(file => !context.FileExists(file.Path))];
	}
}
