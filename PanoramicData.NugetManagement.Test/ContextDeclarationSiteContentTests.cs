using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that <see cref="LocalRepositoryContextBuilder"/> reads the content of every file a
/// dependency can be declared in.
/// </summary>
/// <remarks>
/// <see cref="DependencyMentionScanner"/> treats a declaration site it cannot read as a sighting,
/// because an unread file is not an empty one. That keeps it safe, but it also means every unread
/// declaration site silently disables the obsolete verdict for the whole repository — one
/// <c>.config/dotnet-tools.json</c> nobody fetched is enough to hold every stale pull request in that
/// repository open forever. These tests are what stops that happening quietly.
/// </remarks>
public class ContextDeclarationSiteContentTests(ITestOutputHelper output)
	: TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"declaration-sites-{Guid.NewGuid():N}");

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private RepositoryContext Build()
		=> new LocalRepositoryContextBuilder(NullLogger<LocalRepositoryContextBuilder>.Instance)
			.Build(_root, "panoramicdata/Athonet.Api", new RepoOptions());

	[Theory]
	[InlineData(".config/dotnet-tools.json", """{ "tools": { "nbgv": { "version": "3.10.94" } } }""")]
	[InlineData("src/Legacy/packages.config", """<packages><package id="refit" version="7.2.22" /></packages>""")]
	[InlineData("src/Common.props", """<Project><ItemGroup><PackageVersion Include="refit" Version="7.2.22" /></ItemGroup></Project>""")]
	[InlineData("src/Common.targets", """<Project><ItemGroup><PackageReference Include="refit" /></ItemGroup></Project>""")]
	public void ADeclarationSitesContent_IsRead(string relativePath, string content)
	{
		Write(relativePath, content);

		Build().GetFileContent(relativePath).Should().NotBeNull(
			"a dependency declared here has to be visible to anything asking whether the repository "
			+ "still uses it");
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}
}
