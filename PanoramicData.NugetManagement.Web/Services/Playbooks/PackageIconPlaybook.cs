using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// META-05: set <c>PackageIcon</c> and pack the icon file.
/// </summary>
/// <remarks>
/// Two edits in one file that have to agree: the property naming the icon, and the item that puts the
/// file into the package. A model that makes one and not the other leaves the rule passing and the
/// package broken, so the end state says both explicitly and the example shows them together.
/// </remarks>
public sealed class PackageIconPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "META-05";

	/// <inheritdoc />
	public string Goal
		=> "Add a PackageIcon property and a matching None item to each project file listed in the facts.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files => ["the .csproj files named under 'projects' in the facts"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "Each of those .csproj files contains a PackageIcon element naming an image file, and an "
			+ "ItemGroup containing a None item for that same file with Pack=\"true\". The two name the same "
			+ "file. If the image does not exist in the repository, still add both — do not invent image "
			+ "content.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			Before:

			  <PropertyGroup>
			    <PackageId>Example.Api</PackageId>
			  </PropertyGroup>

			After:

			  <PropertyGroup>
			    <PackageId>Example.Api</PackageId>
			    <PackageIcon>Logo.png</PackageIcon>
			  </PropertyGroup>

			  <ItemGroup>
			    <None Include="Logo.png" Pack="true" PackagePath="\" />
			  </ItemGroup>

			Use Logo.png unless an image file already exists in the project, in which case use that one's
			name in both places. Do not change any other line.
			""";
}
