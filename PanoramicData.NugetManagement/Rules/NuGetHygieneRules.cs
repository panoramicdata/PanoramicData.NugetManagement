using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that snupkg symbol generation is enabled in packable projects.
/// </summary>
public class SnupkgGenerationRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-01";

	/// <inheritdoc />
	public override string RuleName => "snupkg symbol generation enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

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
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is null)
			{
				continue;
			}

			if (!Contains(content, "<IncludeSymbols>true</IncludeSymbols>") ||
				!Contains(content, "<SymbolPackageFormat>snupkg</SymbolPackageFormat>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not enable snupkg generation.",
					"Add <IncludeSymbols>true</IncludeSymbols> and <SymbolPackageFormat>snupkg</SymbolPackageFormat> to the .csproj."));
			}
		}

		return Task.FromResult(Pass("All packable projects have snupkg generation enabled."));
	}
}

/// <summary>
/// Checks that GeneratePackageOnBuild is enabled in packable projects.
/// </summary>
public class GeneratePackageOnBuildRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-02";

	/// <inheritdoc />
	public override string RuleName => "GeneratePackageOnBuild enabled";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<GeneratePackageOnBuild>true</GeneratePackageOnBuild>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not enable GeneratePackageOnBuild.",
					"Add <GeneratePackageOnBuild>true</GeneratePackageOnBuild> to the .csproj."));
			}
		}

		return Task.FromResult(Pass("All packable projects have GeneratePackageOnBuild enabled."));
	}
}

/// <summary>
/// Checks that PackageReadmeFile is set.
/// </summary>
public class PackageReadmeFileRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-03";

	/// <inheritdoc />
	public override string RuleName => "PackageReadmeFile set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		if (!context.Options.IsPackable)
		{
			return Task.FromResult(Pass("Repository is not packable — skipping."));
		}

		var csprojFiles = context.FindFiles(".csproj")
			.Where(f => !f.Contains(".Test", StringComparison.OrdinalIgnoreCase))
			.ToList();

		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (content is not null && !Contains(content, "<PackageReadmeFile>"))
			{
				return Task.FromResult(Fail(
					$"{csproj} does not set PackageReadmeFile.",
					"Add <PackageReadmeFile>README.md</PackageReadmeFile> and pack the README.md via <None Include>."));
			}
		}

		return Task.FromResult(Pass("All packable projects have PackageReadmeFile set."));
	}
}

/// <summary>
/// Checks that NuGetAuditMode is set to All.
/// </summary>
public class NuGetAuditModeRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-04";

	/// <inheritdoc />
	public override string RuleName => "NuGetAuditMode = All";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.NuGetHygiene;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var dirBuild = context.GetFileContent("Directory.Build.props");
		if (Contains(dirBuild, "<NuGetAuditMode>All</NuGetAuditMode>"))
		{
			return Task.FromResult(Pass("NuGetAuditMode is set to All in Directory.Build.props."));
		}

		// Check individual csproj files
		var csprojFiles = context.FindFiles(".csproj");
		foreach (var csproj in csprojFiles)
		{
			var content = context.GetFileContent(csproj);
			if (Contains(content, "<NuGetAuditMode>All</NuGetAuditMode>"))
			{
				return Task.FromResult(Pass($"NuGetAuditMode is set to All in {csproj}."));
			}
		}

		return Task.FromResult(Fail(
			"NuGetAuditMode is not set to All.",
			"Add <NuGetAuditMode>All</NuGetAuditMode> to Directory.Build.props."));
	}
}
