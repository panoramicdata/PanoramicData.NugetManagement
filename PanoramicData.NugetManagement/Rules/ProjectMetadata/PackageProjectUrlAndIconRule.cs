using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageProjectUrl and PackageIcon are set in packable projects.
/// </summary>
public class PackageProjectUrlAndIconRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-04";

	/// <inheritdoc />
	public override string RuleName => "PackageProjectUrl and PackageIcon set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Info;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindNonTestProjectFiles();

		var issues = new List<string>();
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null || IsExplicitlyNonPackable(content))
			{
				continue;
			}

			if (!Contains(content, "<PackageProjectUrl>"))
			{
				issues.Add($"{csproj}: missing PackageProjectUrl");
			}

			if (!Contains(content, "<PackageIcon>"))
			{
				issues.Add($"{csproj}: missing PackageIcon");
			}
		}

		return Task.FromResult(issues.Count == 0
			? Pass("All packable projects have PackageProjectUrl and PackageIcon set.")
			: Fail(
				string.Join("; ", issues),
				new RuleAdvisory
				{
					Summary = "Set <PackageProjectUrl> and <PackageIcon> with a corresponding <None Include> in the .csproj.",
					Detail = $"The following issues were found: {string.Join("; ", issues)}. Add `<PackageProjectUrl>` and `<PackageIcon>` to the `<PropertyGroup>` and include the icon file via `<None Include>` in an `<ItemGroup>`.",
					Data = new() { ["issues"] = issues.ToArray() }
				}));
	}
}
