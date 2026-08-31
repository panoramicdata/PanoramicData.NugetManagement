using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// META-04: add <c>PackageProjectUrl</c> to the packable projects that lack it.
/// </summary>
/// <remarks>
/// The rule's advisory already carries the projects, the property name and the value to use, so the
/// model has to do nothing but place a known string in a known file. That is exactly the shape of task
/// a small model gets right, which makes this a good place to start.
/// </remarks>
public sealed class PackageProjectUrlPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "META-04";

	/// <inheritdoc />
	public string Goal
		=> "Add a PackageProjectUrl property to each project file listed in the facts below.";

	/// <inheritdoc />
	/// <remarks>
	/// The projects are named in the advisory rather than here, because which ones are missing it varies
	/// by repository. This says where to look for them.
	/// </remarks>
	public IReadOnlyList<string> Files => ["the .csproj files named under 'projects' in the facts"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "Each of those .csproj files contains a PackageProjectUrl element, inside an existing "
			+ "PropertyGroup, whose value is the property_value given in the facts. Nothing else in the "
			+ "file has changed.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			Before:

			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <PackageId>Example.Api</PackageId>
			  </PropertyGroup>

			After:

			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <PackageId>Example.Api</PackageId>
			    <PackageProjectUrl>https://github.com/panoramicdata/Example.Api</PackageProjectUrl>
			  </PropertyGroup>

			Add one line. Do not create a new PropertyGroup. Do not change any other line.
			""";
}
