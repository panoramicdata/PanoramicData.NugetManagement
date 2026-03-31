using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that PackageId is set in packable projects.
/// </summary>
public class PackageIdSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-01";

	/// <inheritdoc />
	public override string RuleName => "PackageId set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<PackageId>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not have PackageId set.",
					"Add <PackageId>YourPackageId</PackageId> to the .csproj."));
			}
		}

		return Task.FromResult(Pass("All packable projects have PackageId set."));
	}
}

/// <summary>
/// Checks that RepositoryUrl is set in packable projects.
/// </summary>
public class RepositoryUrlSetRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-02";

	/// <inheritdoc />
	public override string RuleName => "RepositoryUrl set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<RepositoryUrl>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not have RepositoryUrl set.",
					"Add <RepositoryUrl>https://github.com/org/repo</RepositoryUrl> to the .csproj."));
			}
		}

		return Task.FromResult(Pass("All packable projects have RepositoryUrl set."));
	}
}

/// <summary>
/// Checks that Authors and Company are set in Directory.Build.props.
/// </summary>
public class AuthorsAndCompanyRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-03";

	/// <inheritdoc />
	public override string RuleName => "Authors and Company in Directory.Build.props";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var expected = context.Options.ExpectedCopyrightHolder;
		var content = context.GetFileContent("Directory.Build.props");
		if (content is null)
		{
			return Task.FromResult(Fail(
				"Directory.Build.props not found.",
				"Create Directory.Build.props with <Authors> and <Company>."));
		}

		var hasAuthors = Contains(content, "<Authors>");
		var hasCompany = Contains(content, "<Company>");

		return Task.FromResult(hasAuthors && hasCompany
			? Pass("Authors and Company are set in Directory.Build.props.")
			: Fail(
				"Directory.Build.props is missing <Authors> and/or <Company>.",
				$"Add <Authors>{expected}</Authors> and <Company>{expected}</Company>."));
	}
}

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

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase));

		var issues = new List<string>();
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null)
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
				"Set <PackageProjectUrl> and <PackageIcon> with a corresponding <None Include> in the .csproj."));
	}
}
