using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that a repository which publishes to NuGet has at least one project saying so, since every
/// other packaging rule has nothing to check until one does.
/// </summary>
/// <remarks>
/// This exists to stop a gap being invisible. Packable projects are identified by what they declare —
/// <c>PackageId</c>, <c>GeneratePackageOnBuild</c>, <c>PackAsTool</c> or <c>IsPackable=true</c> — so a
/// repository whose package project declares none of them has every packaging rule skipped. Reported
/// as a pass, that reads as compliance; reported here, it is one finding that names the fix.
/// </remarks>
public class PackageProjectIdentifiableRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "PKG-10";

	/// <inheritdoc />
	public override string RuleName => "Package project can be identified";

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

		var packable = context.FindPackableProjectFiles().ToList();
		if (packable.Count > 0)
		{
			return Task.FromResult(Pass(packable.Count == 1
				? $"{packable[0]} is identified as the published project."
				: $"{packable.Count} published projects identified: {string.Join(", ", packable)}."));
		}

		var candidates = context.FindNonTestProjectFiles().ToList();
		if (candidates.Count == 0)
		{
			return Task.FromResult(NotApplicable("No non-test projects found; nothing could be published."));
		}

		return Task.FromResult(Fail(
			$"No project declares itself published, so every packaging rule is skipped. Candidates: {string.Join(", ", candidates)}.",
			new RuleAdvisory
			{
				Summary = "Declare which project is published, or mark the repository as not packable.",
				Detail = $$"""
					This repository is treated as publishing to NuGet, but none of its non-test projects
					declares `PackageId`, `GeneratePackageOnBuild`, `PackAsTool` or `IsPackable=true`. Until
					one does, PKG-01/02/03, META-01/03/04 and LIC-02 have nothing to check and report as
					not-applicable rather than passing.

					Candidates found: {{string.Join(", ", candidates)}}.

					Any one of these resolves it:

					- Add `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` and `<PackageId>` to the
					  project that is published — which those rules would ask for anyway.
					- Set `"isPackable": false` in the repository's options if it publishes nothing.
					- Nominate the project in `{{NugetManagementRepositoryConfig.FileName}}`:
					  `"projects": { "Path/To/Project.csproj": { "packagingTreatment": "Include" } }`.

					Naming a project after the repository used to be enough to identify it. It no longer is,
					because it silently skipped every packaging rule for repositories that name theirs
					differently.
					""",
				Data = new()
				{
					["candidates"] = candidates.ToArray(),
					["config_file"] = NugetManagementRepositoryConfig.FileName
				}
			}));
	}
}
