using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// PKG-13: swap FluentAssertions for AwesomeAssertions.
/// </summary>
/// <remarks>
/// The one thing a model gets wrong here is carrying the version across with the name. AwesomeAssertions
/// does not continue FluentAssertions' version line, so `AwesomeAssertions 6.10.0` does not exist and the
/// restore fails — which is why both the end state and the example say it twice.
/// </remarks>
public sealed class FluentAssertionsMigrationPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "PKG-13";

	/// <inheritdoc />
	public string Goal
		=> "Replace every FluentAssertions package reference and using directive with AwesomeAssertions.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files =>
	[
		"Directory.Packages.props, if the repository has one",
		"the .csproj files named under 'fluent_assertions_references' in the facts",
		"every .cs file containing 'using FluentAssertions'"
	];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "No file mentions FluentAssertions. Every package reference that named it names "
			+ "AwesomeAssertions instead (and FluentAssertions.Analyzers becomes "
			+ "AwesomeAssertions.Analyzers), every `using FluentAssertions;` reads "
			+ "`using AwesomeAssertions;`, and the version is one that exists for the new package — do "
			+ "not reuse the FluentAssertions version. No assertion is rewritten: the API is identical, "
			+ "so any change to a test body means something has gone wrong.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			In Directory.Packages.props:

			-  <PackageVersion Include="FluentAssertions" Version="6.10.0" />
			+  <PackageVersion Include="AwesomeAssertions" Version="9.6.0" />

			In the test project's .csproj:

			-  <PackageReference Include="FluentAssertions" />
			+  <PackageReference Include="AwesomeAssertions" />

			In each test file:

			-  using FluentAssertions;
			+  using AwesomeAssertions;

			The version above is an example. Take the version another repository already uses rather than
			inventing one, and never carry 6.10.0 across — there is no AwesomeAssertions 6.10.0.
			""";
}
