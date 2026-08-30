using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for TST-09, which requires a test project that reads user secrets to declare a
/// <c>UserSecretsId</c>. Without one the project has no secrets store of its own, so the credentials
/// its integration tests need cannot be set — and a second clone of the same repository cannot
/// inherit them.
/// </summary>
public class UserSecretsIdRuleTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _testProjectPath = "Acme.Lib.Test/Acme.Lib.Test.csproj";

	private const string _usesUserSecretsWithoutId = """
		<Project>
		  <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
		  <ItemGroup><PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" /></ItemGroup>
		</Project>
		""";

	private const string _usesUserSecretsWithId = """
		<Project>
		  <PropertyGroup>
		    <IsTestProject>true</IsTestProject>
		    <UserSecretsId>e63e60d9-8d7c-4e1e-b5c2-f1a5e3b7f4a2</UserSecretsId>
		  </PropertyGroup>
		  <ItemGroup><PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" /></ItemGroup>
		</Project>
		""";

	[Fact]
	public async Task TST09_ShouldNotApply_WhenTheRepositoryHasNoTestProject()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			["Acme.Lib/Acme.Lib.csproj"] = "<Project><ItemGroup /></Project>"
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST09_ShouldNotApply_WhenNoTestProjectReadsUserSecrets()
	{
		// The overwhelming majority of repositories: unit tests with no credentials to hold. The rule
		// must stay silent for them rather than demanding an id nothing would read.
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = "<Project><ItemGroup /></Project>"
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeFalse();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST09_ShouldFail_WhenATestProjectReadsUserSecretsWithNoId()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = _usesUserSecretsWithoutId
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeTrue();
		result.Passed.Should().BeFalse();
		result.Message.Should().Contain(_testProjectPath);
	}

	[Fact]
	public async Task TST09_ShouldFail_WhenTheIdIsDeclaredButEmpty()
	{
		// An empty element is how `dotnet user-secrets init` looks when it has been half-applied, and
		// it resolves to no store at all — so it is a failure, not a declaration.
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = """
				<Project>
				  <PropertyGroup>
				    <IsTestProject>true</IsTestProject>
				    <UserSecretsId>  </UserSecretsId>
				  </PropertyGroup>
				  <ItemGroup><PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" /></ItemGroup>
				</Project>
				"""
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
	}

	[Fact]
	public async Task TST09_ShouldPass_WhenTheTestProjectDeclaresAnId()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = _usesUserSecretsWithId
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.IsApplicable.Should().BeTrue();
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST09_ShouldPass_WhenTheIdIsNotAGuid()
	{
		// A project-name id is legal and self-uniquifying — arguably safer than a GUID, since GUIDs
		// are what get copy-pasted between repositories. The rule must not push people off it.
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = """
				<Project>
				  <PropertyGroup>
				    <IsTestProject>true</IsTestProject>
				    <UserSecretsId>Acme.Lib.Test</UserSecretsId>
				  </PropertyGroup>
				  <ItemGroup><PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" /></ItemGroup>
				</Project>
				"""
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public async Task TST09_ShouldFail_WhenOnlyOneOfSeveralTestProjectsDeclaresAnId()
	{
		// The id is per project, not per repository: one project's store does nothing for another's.
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = _usesUserSecretsWithId,
			["Acme.Other.Test/Acme.Other.Test.csproj"] = _usesUserSecretsWithoutId
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeFalse();
		result.Message.Should().Contain("Acme.Other.Test");
		result.Message.Should().NotContain(_testProjectPath);
	}

	[Fact]
	public async Task TST09_ShouldIgnoreANonTestProjectWithNoId()
	{
		// A web application reads user secrets too, but its secrets are a deployment concern rather
		// than something the test run depends on. TST-09 speaks only for the test projects.
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = _usesUserSecretsWithId,
			["Acme.Web/Acme.Web.csproj"] = _usesUserSecretsWithoutId
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void TST09_ShouldBeAnError_BecauseTheTestsCannotAuthenticateWithoutIt()
	{
		new UserSecretsIdRule().Severity.Should().Be(AssessmentSeverity.Error);
		new UserSecretsIdRule().Category.Should().Be(AssessmentCategory.Testing);
	}

	[Fact]
	public async Task TST09_ShouldAdviseRunningUserSecretsInit()
	{
		var context = CreateContext(new Dictionary<string, string>
		{
			[_testProjectPath] = _usesUserSecretsWithoutId
		});

		var result = await new UserSecretsIdRule().EvaluateAsync(context, CancellationToken.None);

		result.Advisory!.Detail.Should().Contain("dotnet user-secrets init");
	}

	private static RepositoryContext CreateContext(Dictionary<string, string> files) => new()
	{
		FullName = "test-org/Acme.Lib",
		Name = "Acme.Lib",
		DefaultBranch = "main",
		CurrentBranch = "main",
		Options = new RepoOptions(),
		FilePaths = [.. files.Keys],
		FileContents = files
	};
}
