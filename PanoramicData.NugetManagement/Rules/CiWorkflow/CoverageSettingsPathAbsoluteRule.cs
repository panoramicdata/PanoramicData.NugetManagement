using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// A workflow that runs the test host directly passes <c>--coverage-settings</c> an absolute path.
/// </summary>
/// <remarks>
/// Microsoft.Testing.Extensions.CodeCoverage 18.12 rejects a relative path in that mode, so the
/// coverage job fails before it uploads anything, CI goes red and Codacy holds no coverage figure —
/// which TST-10 then reports without saying why. Found on TeamViewer.Api, where it failed every push.
/// <c>dotnet test</c> accepts a relative path, so those steps are not reported.
/// </remarks>
public class CoverageSettingsPathAbsoluteRule : RuleBase
{
	/// <inheritdoc />
	public override string RuleId => "CI-16";

	/// <inheritdoc />
	public override string RuleName => "CI passes an absolute coverage settings path";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.CiCd;

	/// <summary>
	/// An error: the job fails outright, so CI is red until it is fixed.
	/// </summary>
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var workflows = context.FilePaths
			.Where(IsWorkflowFile)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (workflows.Count == 0)
		{
			return Task.FromResult(NotApplicable("No workflow files found."));
		}

		// Null means the file was listed but never fetched, which says nothing about its contents.
		var affected = workflows
			.Where(path => context.GetFileContent(path) is { } content
				&& CoverageSettingsPathEditor.MakeAbsolute(content) is not null)
			.ToList();

		if (affected.Count == 0)
		{
			return Task.FromResult(Pass("No workflow passes a relative --coverage-settings path to a directly run test host."));
		}

		return Task.FromResult(Fail(
			$"{string.Join(", ", affected.Select(Path.GetFileName))} pass a relative --coverage-settings path to the test host, which the coverage extension rejects.",
			new RuleAdvisory
			{
				Summary = "Pass --coverage-settings an absolute path: ${{ github.workspace }}/coverage.config.",
				Detail = """
					Microsoft.Testing.Extensions.CodeCoverage 18.12 does not resolve a relative
					--coverage-settings path when the test application is run directly. It stops with
					"Provided settings file doesn't exist or is invalid", so the coverage job fails before it
					uploads anything and Codacy holds no figure for the repository.

					Prefix the path with `${{ github.workspace }}/`. A step that uses `dotnet test` is
					unaffected and does not need changing.
					""",
				Data = new()
				{
					["workflow_files"] = affected.ToArray()
				}
			}));
	}

	private static bool IsWorkflowFile(string path)
		=> path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
			&& (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
