using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// TST-10: get the repository's coverage into Codacy by adding a coverage job to its CI workflow.
/// </summary>
/// <remarks>
/// Written because the advisory's own instruction was "copy the job from another repository", which a
/// session confined to one clone cannot do — the first logged run spent twelve turns hunting for a file
/// it had no way to read. The job is therefore spelled out here.
/// <para>
/// TST-10 is graded by Codacy on the published branch, so the session is judged on whether the
/// workflow changed and the repository still builds. Whether the upload then works is established by
/// the next CI run, and needs a <c>CODACY_PROJECT_TOKEN</c> secret that no file edit can supply.
/// </para>
/// </remarks>
public sealed class CoverageUploadPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "TST-10";

	/// <inheritdoc />
	public string Goal
		=> "Add a job named coverage to the CI workflow that runs the tests with coverage and uploads the "
			+ "Cobertura report to Codacy.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files => [".github/workflows/ci.yml"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "The workflow file has a job that runs the test project with coverage, writing a Cobertura XML "
			+ "file, followed by a step using codacy/codacy-coverage-reporter-action with the "
			+ "CODACY_PROJECT_TOKEN secret and that same file as its coverage-reports. No other job and no "
			+ "other file is changed. Do not edit project files, even if the build complains about them.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			Add this job under the existing jobs: key, using the same runs-on, checkout and setup-dotnet
			steps the other jobs in the file already use. Change only the test project path.

			  coverage:
			    runs-on: ubuntu-latest
			    steps:
			    - uses: actions/checkout@v4
			    - uses: actions/setup-dotnet@v4
			      with:
			        dotnet-version: '10.0.x'
			    - name: Run tests with coverage
			      run: dotnet test --project Example.Test --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
			    - name: Upload coverage to Codacy
			      uses: codacy/codacy-coverage-reporter-action@v1
			      with:
			        project-token: ${{ secrets.CODACY_PROJECT_TOKEN }}
			        coverage-reports: '**/coverage.cobertura.xml'

			The secret itself is added by a person in the repository's settings. You cannot add it and
			must not try.
			""";
}
