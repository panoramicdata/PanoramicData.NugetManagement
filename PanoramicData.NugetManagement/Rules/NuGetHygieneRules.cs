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
					new RuleAdvisory
					{
						Summary = "Add <IncludeSymbols>true</IncludeSymbols> and <SymbolPackageFormat>snupkg</SymbolPackageFormat> to the .csproj.",
						Detail = $"The project `{csproj}` does not enable snupkg symbol package generation. Add both `<IncludeSymbols>true</IncludeSymbols>` and `<SymbolPackageFormat>snupkg</SymbolPackageFormat>` to a `<PropertyGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "IncludeSymbols",
							["property_value"] = "true"
						}
					}));
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
					new RuleAdvisory
					{
						Summary = "Add <GeneratePackageOnBuild>true</GeneratePackageOnBuild> to the .csproj.",
						Detail = $"The project `{csproj}` does not enable `GeneratePackageOnBuild`. Add `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` to a `<PropertyGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "GeneratePackageOnBuild",
							["property_value"] = "true"
						}
					}));
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
					new RuleAdvisory
					{
						Summary = "Add <PackageReadmeFile>README.md</PackageReadmeFile> and pack the README.md via <None Include>.",
						Detail = $"The project `{csproj}` does not set `PackageReadmeFile`. Add `<PackageReadmeFile>README.md</PackageReadmeFile>` to a `<PropertyGroup>` and include `<None Include=\"..\\README.md\" Pack=\"true\" PackagePath=\"\\\"/>` in an `<ItemGroup>`.",
						Data = new()
						{
							["file"] = csproj,
							["remediation_type"] = "ensure_csproj_property",
							["property_name"] = "PackageReadmeFile",
							["property_value"] = "README.md"
						}
					}));
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
			new RuleAdvisory
			{
				Summary = "Add <NuGetAuditMode>All</NuGetAuditMode> to Directory.Build.props.",
				Detail = "No project or `Directory.Build.props` sets `NuGetAuditMode` to `All`. Add `<NuGetAuditMode>All</NuGetAuditMode>` to `Directory.Build.props` to enable transitive NuGet vulnerability auditing.",
				Data = new()
			{
				["file"] = "Directory.Build.props",
				["remediation_type"] = "ensure_xml_property",
				["property_name"] = "NuGetAuditMode",
				["property_value"] = "All"
			}
			}));
	}
}
