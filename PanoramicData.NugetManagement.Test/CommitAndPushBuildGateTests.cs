using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Remediations;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// #196: a remediation whose result does not build must never be committed to the default branch.
/// </summary>
/// <remarks>
/// Real git and a real <c>dotnet build</c>, with no network. The origin is a bare repository nested in
/// the clone and named by the relative URL <c>owner/name</c>, which the identity check accepts. The
/// project is a bare MSBuild file whose Build target fails when SECURITY.md exists, so applying the
/// real COM-01 remediation is what breaks it.
/// </remarks>
public sealed class CommitAndPushBuildGateTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private const string Owner = "panoramicdata";
	private const string Name = "gate-test";
	private const string Identity = $"{Owner}/{Name}";

	private const string BrokenBySecurityMd =
		"<Project DefaultTargets=\"Build\"><Target Name=\"Restore\" /><Target Name=\"Build\">"
		+ "<Error Condition=\"Exists('SECURITY.md')\" Text=\"SECURITY.md breaks this build\" /></Target></Project>";

	private const string AlwaysBuilds =
		"<Project DefaultTargets=\"Build\"><Target Name=\"Restore\" /><Target Name=\"Build\" /></Project>";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", Guid.NewGuid().ToString("n"));

	private string Clone => Path.Combine(_root, Owner, Name);

	private string Origin => Path.Combine(Clone, Owner, Name);

	[Fact]
	public async Task CommitAndPush_WhenTheRemediationBreaksTheBuild_CommitsNothingAndRestoresTheClone()
	{
		var dashboard = CreateClone(BrokenBySecurityMd);
		var row = Row();
		var headBefore = Git(Clone, "rev-parse", "HEAD");
		var originBefore = Git(Origin, "rev-parse", "main");

		var applied = await dashboard.ApplyRuleRemediationAsync(row, SecurityMdMissing(), cancellationToken: TestContext.Current.CancellationToken);
		applied.Should().NotBeEmpty("the remediation has to have written something for the gate to have anything to refuse");

		var push = await dashboard.CommitAndPushAsync(row, "NuGet governance remediation for test", cancellationToken: TestContext.Current.CancellationToken);

		push.Success.Should().BeFalse();
		push.WasRefused.Should().BeTrue();
		push.ChangesReverted.Should().BeTrue();
		push.RefusalReason.Should().Contain("does not build").And.Contain("SECURITY.md");
		Git(Clone, "rev-parse", "HEAD").Should().Be(headBefore, "nothing may be committed");
		Git(Origin, "rev-parse", "main").Should().Be(originBefore, "nothing may be pushed");
		Git(Clone, "status", "--porcelain").Should().BeEmpty("the remediation's changes are reverted");
		File.Exists(Path.Combine(Clone, "SECURITY.md")).Should().BeFalse();
		Git(Clone, "stash", "list").Should().NotBeEmpty("the reverted changes are set aside, not thrown away");
	}

	[Fact]
	public async Task CommitAndPush_WhenTheRemediationBuilds_CommitsAndPushesAsBefore()
	{
		var dashboard = CreateClone(AlwaysBuilds);
		var row = Row();
		var originBefore = Git(Origin, "rev-parse", "main");

		await dashboard.ApplyRuleRemediationAsync(row, SecurityMdMissing(), cancellationToken: TestContext.Current.CancellationToken);

		var push = await dashboard.CommitAndPushAsync(row, "NuGet governance remediation for test", cancellationToken: TestContext.Current.CancellationToken);

		push.Success.Should().BeTrue(push.RefusalReason);
		Git(Origin, "rev-parse", "main").Should().NotBe(originBefore);
		Git(Origin, "show", "--name-only", "--format=", "main").Should().Contain("SECURITY.md");
		Git(Clone, "status", "--porcelain").Should().BeEmpty();
	}

	[Fact]
	public async Task CommitAndPush_WithNothingToCommit_DoesNotBuild()
	{
		// A failing build that would refuse if it ran: a clean, level clone has nothing to verify.
		var dashboard = CreateClone(BrokenBySecurityMd);
		File.WriteAllText(Path.Combine(Clone, "SECURITY.md"), "already here\n");
		Git(Clone, "add", "-A");
		Git(Clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "broken already");
		Git(Clone, "push", "-q", "origin", "main");

		var push = await dashboard.CommitAndPushAsync(Row(), "NuGet governance remediation for test", cancellationToken: TestContext.Current.CancellationToken);

		push.WasRefused.Should().BeFalse(push.RefusalReason);
	}

	private RepositoryDashboardRow Row() => new()
	{
		RepositoryFullName = Identity,
		Organization = Owner,
		LocalPath = Clone,
		IsClonedLocally = true,
		Packages = [new() { PackageId = "Gate.Test", LatestVersion = "1.0.0" }]
	};

	private static RuleResult SecurityMdMissing() => new()
	{
		RuleId = "COM-01",
		RuleName = "SECURITY.md exists",
		Category = AssessmentCategory.CommunityHealth,
		Severity = AssessmentSeverity.Warning,
		Passed = false,
		Message = "SECURITY.md not found at repository root.",
		Advisory = new RuleAdvisory
		{
			Summary = "Create SECURITY.md.",
			Detail = "Create SECURITY.md with the standard security policy content.",
			Data = new()
			{
				["expected_path"] = "SECURITY.md",
				["template_content"] = "# Security Policy\n"
			}
		}
	};

	private DashboardService CreateClone(string project)
	{
		Directory.CreateDirectory(Clone);
		Git(Clone, "init", "-q", "-b", "main");
		Git(Clone, "config", "core.autocrlf", "false");
		File.WriteAllText(Path.Combine(Clone, "build.proj"), project);
		Git(Clone, "add", "-A");
		Git(Clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "first");

		Directory.CreateDirectory(Origin);
		Git(Origin, "init", "-q", "--bare", "-b", "main");
		File.AppendAllText(Path.Combine(Clone, ".git", "info", "exclude"), $"{Owner}/\n");
		Git(Clone, "remote", "add", "origin", Identity);
		Git(Clone, "push", "-q", "-u", "origin", "main");

		// CommitAndPushAsync commits with the ambient identity, which a CI runner may not have.
		Git(Clone, "config", "user.name", "Test");
		Git(Clone, "config", "user.email", "test@example.com");

		return CreateDashboardService();
	}

	private DashboardService CreateDashboardService()
	{
		var appSettings = Options.Create(new AppSettings { LocalReposRoot = _root });
		var runtimeSettings = new RuntimeSettingsService(appSettings, NullLogger<RuntimeSettingsService>.Instance);
		var localRepo = new LocalRepoService(runtimeSettings, NullLogger<LocalRepoService>.Instance);
		var cache = new DashboardCacheService(NullLogger<DashboardCacheService>.Instance, Path.Combine(_root, "dashboard-cache.json"));

		return new DashboardService(
			new NuGetDiscoveryService(
				appSettings,
				new NuspecRepositoryResolver(new NoopHttpClientFactory(), NullLogger<NuspecRepositoryResolver>.Instance),
				NullLogger<NuGetDiscoveryService>.Instance),
			new PublishedVersionRefresher(new NoopPublishedVersionSource()),
			cache,
			localRepo,
			new RemediationRegistry(),
			new RegressionGuardService(localRepo, NullLogger<RegressionGuardService>.Instance),
			runtimeSettings,
			new NuGetOwnedPackageCatalog(null),
			new RuleWaiverCatalog(null),
			appSettings,
			new NoopCodacyAnalysisStateService(),
			NullLogger<DashboardService>.Instance);
	}

	private static string Git(string workingDirectory, params string[] arguments)
	{
		var (exitCode, output) = LocalRepoService
			.RunCommandAsync(workingDirectory, "git", arguments)
			.GetAwaiter()
			.GetResult();

		return exitCode == 0
			? output.Trim()
			: throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
	}

	/// <inheritdoc />
	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				// Git marks its object files read-only, which Directory.Delete will not remove.
				foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
				{
					File.SetAttributes(file, FileAttributes.Normal);
				}

				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A leftover temp directory is not worth failing a test over.
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>Never called: nothing here talks to NuGet.</summary>
	private sealed class NoopHttpClientFactory : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
			=> throw new InvalidOperationException($"No HTTP client was expected, but '{name}' was requested.");
	}

	/// <summary>Never called: nothing here reads published versions.</summary>
	private sealed class NoopPublishedVersionSource : IPublishedVersionSource
	{
		public Task<string?> GetLatestPublishedVersionAsync(string packageId, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			throw new InvalidOperationException($"No published-version lookup was expected, but '{packageId}' was asked for.");
		}
	}
}
