using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Services;
using System.Net;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the difference between a nuspec that declares no repository and a nuspec we failed to
/// read. Collapsing the two is what filed eight correctly-declared packages as ungoverned, each
/// blamed for an omission it had not made.
/// </summary>
public class NuspecRepositoryResolverTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string _declaredNuspec = """
		<?xml version="1.0"?>
		<package><metadata>
			<id>ConnectWise.Manage.Api</id>
			<repository type="git" url="https://github.com/panoramicdata/ConnectWise.Manage.Api" />
		</metadata></package>
		""";

	private const string _silentNuspec = """
		<?xml version="1.0"?>
		<package><metadata><id>JiraSetup</id></metadata></package>
		""";

	[Fact]
	public async Task ADeclaredRepositoryShouldResolve()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Nuspec(_declaredNuspec));

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		resolution.RepositoryUrl.Should().Be("https://github.com/panoramicdata/ConnectWise.Manage.Api");
	}

	[Fact]
	public async Task ANuspecDeclaringNothingShouldBeNotDeclared()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Nuspec(_silentNuspec));

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.NotDeclared);
	}

	[Fact]
	public async Task AGitHubProjectUrlShouldStandInForASilentNuspec()
	{
		var resolution = await ResolveAsync(
			StubHttpMessageHandler.Nuspec(_silentNuspec),
			projectUrl: "https://github.com/panoramicdata/Meraki.Api");

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		resolution.RepositoryUrl.Should().Be("https://github.com/panoramicdata/Meraki.Api");
	}

	[Fact]
	public async Task AnUnreadableNuspecShouldBeLookupFailedRatherThanNotDeclared()
	{
		var resolution = await ResolveAsync(StubHttpMessageHandler.Throws());

		resolution.Outcome.Should().Be(
			RepositoryResolutionOutcome.LookupFailed,
			"a package we could not ask about has not been shown to declare nothing");
		resolution.Error.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task ATransientFailureShouldBeRetried()
	{
		var handler = new StubHttpMessageHandler(
			StubHttpMessageHandler.Throws(),
			StubHttpMessageHandler.Throws(),
			StubHttpMessageHandler.Nuspec(_declaredNuspec));

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.Resolved);
		handler.CallCount.Should().Be(3);
	}

	[Fact]
	public async Task ThreeFailuresShouldGiveUp()
	{
		var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Throws());

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.LookupFailed);
		handler.CallCount.Should().Be(3, "three attempts, then the answer is that we do not know");
	}

	[Fact]
	public async Task AMissingNuspecShouldNotBeRetried()
	{
		var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.NotFound));

		var resolution = await ResolveAsync(handler);

		resolution.Outcome.Should().Be(RepositoryResolutionOutcome.NotDeclared);
		handler.CallCount.Should().Be(1, "a 404 is a definite answer, not a flaky one");
	}

	private static Task<RepositoryResolution> ResolveAsync(
		Func<HttpResponseMessage> response,
		string? projectUrl = null)
		=> ResolveAsync(new StubHttpMessageHandler(response), projectUrl);

	private static Task<RepositoryResolution> ResolveAsync(
		StubHttpMessageHandler handler,
		string? projectUrl = null)
	{
		var resolver = new NuspecRepositoryResolver(
			new StubHttpClientFactory(handler),
			NullLogger<NuspecRepositoryResolver>.Instance)
		{
			RetryDelay = TimeSpan.Zero
		};

		return resolver.ResolveAsync("ConnectWise.Manage.Api", "3.1.0", projectUrl, CancellationToken.None);
	}
}
