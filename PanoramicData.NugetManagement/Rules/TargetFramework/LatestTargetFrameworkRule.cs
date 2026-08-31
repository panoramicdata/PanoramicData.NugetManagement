using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that projects target the latest .NET version.
/// </summary>
public class LatestTargetFrameworkRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "TFM-01";

	/// <inheritdoc />
	public override string RuleName => "Latest .NET target framework";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.TargetFramework;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var csprojFiles = context.FindFiles(".csproj").ToList();
		var outdated = new List<string>();
		var exempt = new List<string>();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null)
			{
				continue;
			}

			// A Roslyn analyzer or source generator is loaded in-process by the compiler, whose host is
			// .NET Framework under Visual Studio and MSBuild.exe. Targeting the latest framework is
			// never right for one, so this is not a suppression but the rule not applying: pushing
			// Meraki.Api's generator to net10.0 broke that build, and in Visual Studio the generator
			// stops loading with no diagnostic at all.
			if (context.IsCompilerExtensionProject(csproj))
			{
				exempt.Add(csproj);
				continue;
			}

			if (!HasMsBuildProperty(content, "TargetFramework", Standards.LatestTargetFramework))
			{
				outdated.Add(csproj);
			}
		}

		var exemptNote = exempt.Count == 0
			? string.Empty
			: $" Compiler extensions exempt (they must stay on netstandard2.0): {string.Join(", ", exempt)}.";

		return Task.FromResult(outdated.Count == 0
			? Pass($"All projects target {Standards.LatestTargetFramework}.{exemptNote}")
			: Fail(
				$"The following projects do not target {Standards.LatestTargetFramework}: {string.Join(", ", outdated)}.{exemptNote}",
				new RuleAdvisory
				{
					Summary = $"Update <TargetFramework> to {Standards.LatestTargetFramework} in all .csproj files.",
					Detail = $"The following projects do not target `{Standards.LatestTargetFramework}`: {string.Join(", ", outdated)}. Update the `<TargetFramework>` element in each `.csproj` file.",
					Data = new()
					{
						["projects"] = outdated.ToArray(),
						["latest_tfm"] = Standards.LatestTargetFramework,
						["remediation_type"] = "ensure_csproj_property",
						["property_name"] = "TargetFramework",
						["property_value"] = Standards.LatestTargetFramework
					}
				}));
	}
}
