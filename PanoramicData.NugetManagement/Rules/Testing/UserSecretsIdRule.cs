using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that every test project reading user secrets declares a <c>UserSecretsId</c>.
/// </summary>
/// <remarks>
/// The id is what names the secrets store, and it lives in the .csproj — so it travels with the
/// repository. Declare one and every clone of that repository, on a given machine, resolves to the
/// same store: credentials set once are found by the tests wherever they are checked out. Leave it
/// out and the project has no store at all, so there is nowhere to put the credentials its
/// integration tests need.
///
/// Only test projects are in scope. An application reading user secrets is doing so for its own
/// local run, which is a deployment concern rather than something a governed test run depends on.
/// </remarks>
public class UserSecretsIdRule : RuleBase
{
	/// <summary>
	/// The package a project references when it reads user secrets. Taken as the signal in preference
	/// to searching sources for <c>AddUserSecrets</c>, because assessment fetches project files rather
	/// than every .cs file in the repository.
	/// </summary>
	private const string _userSecretsPackage = "Microsoft.Extensions.Configuration.UserSecrets";

	/// <inheritdoc />
	public override string RuleId => "TST-09";

	/// <inheritdoc />
	public override string RuleName => "Test projects using user secrets declare a UserSecretsId";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Testing;

	// Error, not Warning: without an id there is no store, so the integration tests that reach for
	// these credentials cannot pass on any machine.
	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		// Judged per project rather than per repository: one project's store does nothing for
		// another's, so a repository with two test projects needs two declarations.
		var usingUserSecrets = context
			.FindTestProjectFiles()
			.Where(testProject => ReferencesPackageDirectly(context.GetFileContent(testProject), _userSecretsPackage))
			.ToList();

		if (usingUserSecrets.Count == 0)
		{
			return Task.FromResult(NotApplicable(
				"No test project reads user secrets, so there is no secrets store to name."));
		}

		var missing = usingUserSecrets
			.Where(testProject => !DeclaresUserSecretsId(context.GetFileContent(testProject)))
			.ToList();

		if (missing.Count == 0)
		{
			return Task.FromResult(Pass(
				$"Every test project reading user secrets declares a UserSecretsId ({usingUserSecrets.Count})."));
		}

		var projectList = string.Join(", ", missing);

		return Task.FromResult(Fail(
			$"Reads user secrets but declares no UserSecretsId: {projectList}.",
			new RuleAdvisory
			{
				Summary = "Run dotnet user-secrets init in each test project that reads user secrets.",
				Detail = $"""
					These test projects reference `{_userSecretsPackage}` but declare no `UserSecretsId`:
					{string.Join("\n", missing.Select(project => $"- `{project}`"))}

					Run `dotnet user-secrets init` in each one. That adds a `UserSecretsId` to the .csproj,
					which is what names the project's secrets store — without it there is nowhere to put the
					credentials the tests read, and `AddUserSecrets` contributes nothing.

					Because the id lives in the .csproj it is committed and travels with the repository, so
					every clone on a machine shares one store: credentials set once are found wherever the
					repository is checked out.

					Any non-empty id will do. `dotnet user-secrets init` generates a GUID, but a value naming
					the project is equally valid and harder to duplicate by accident — do not replace an
					existing one.
					""",
				// No remediation_type: the fix needs a value that is unique to this project, which a
				// static payload cannot supply. Left to the AI-assisted path.
				Data = new()
				{
					["projects"] = missing.ToArray()
				}
			}));
	}

	/// <summary>
	/// Whether the project declares a usable <c>UserSecretsId</c>. A declaration with an empty value
	/// resolves to no store, so it does not count.
	/// </summary>
	private static bool DeclaresUserSecretsId(string? projectContent)
		=> MsBuildProperties
			.TryGetValues(projectContent, "UserSecretsId")
			?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;
}
