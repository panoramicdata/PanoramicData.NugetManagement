using Microsoft.Extensions.Logging.Abstractions;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="UnlistedRepositoryResolver"/>: finding out what became of a repository that packages
/// still point at but that GitHub's organisation list no longer shows under that name.
/// </summary>
public class UnlistedRepositoryResolverTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static IReadOnlySet<string> Published(params string[] names)
		=> new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyCollection<GitHubRepositoryInfo> Listed(params string[] names)
		=> [.. names.Select(name => new GitHubRepositoryInfo(name, $"https://github.com/{name}", false, false))];

	private static Task<IReadOnlyList<GitHubRepositoryInfo>> ResolveAsync(
		string organization,
		IReadOnlySet<string> published,
		IReadOnlyCollection<GitHubRepositoryInfo> listed,
		Func<string, CancellationToken, Task<ResolvedRepository?>> lookup)
		=> UnlistedRepositoryResolver.ResolveAsync(
			organization,
			published,
			listed,
			lookup,
			NullLogger.Instance,
			TestContext.Current.CancellationToken);

	[Fact]
	public async Task ARenamedAndArchivedRepository_IsReportedArchivedUnderTheNameThePackageRecords()
	{
		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/PanoramicData.DocMagic"),
			Listed("panoramicdata/PanoramicData.DocMagic.Archived", "panoramicdata/Other.Api"),
			(_, _) => Task.FromResult<ResolvedRepository?>(
				new ResolvedRepository("panoramicdata/PanoramicData.DocMagic.Archived", IsArchived: true, IsFork: false)));

		var info = resolved.Should().ContainSingle().Subject;
		info.FullName.Should().Be("panoramicdata/PanoramicData.DocMagic",
			"BuildRows matches by the name the package records, so that is the name the answer must carry");
		info.IsArchived.Should().BeTrue();
		info.Url.Should().Be("https://github.com/panoramicdata/PanoramicData.DocMagic.Archived",
			"the link should go where the repository lives now");
	}

	[Fact]
	public async Task ARepositoryTheListAlreadyShows_IsNotLookedUp()
	{
		var lookups = 0;

		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/Meraki.Reporting.Api"),
			Listed("panoramicdata/meraki.reporting.api"),
			(_, _) =>
			{
				lookups++;
				return Task.FromResult<ResolvedRepository?>(null);
			});

		resolved.Should().BeEmpty();
		lookups.Should().Be(0, "the list already says what it needs to, and a lookup per package would burn the API budget");
	}

	[Fact]
	public async Task APackageRepositoryInAnotherOrganisation_IsLeftToThatOrganisation()
	{
		var lookups = 0;

		await ResolveAsync(
			"panoramicdata",
			Published("someone-else/Their.Api"),
			Listed("panoramicdata/Mine.Api"),
			(_, _) =>
			{
				lookups++;
				return Task.FromResult<ResolvedRepository?>(null);
			});

		lookups.Should().Be(0, "this organisation's list says nothing about another organisation's repositories");
	}

	[Fact]
	public async Task ARepositoryThatNoLongerExists_AddsNothingAndDoesNotFail()
	{
		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/Gone.Api"),
			Listed(),
			(_, _) => Task.FromResult<ResolvedRepository?>(null));

		resolved.Should().BeEmpty();
	}

	[Fact]
	public async Task ALookupThatFails_LeavesThatRepositoryAsItWasAndCarriesOn()
	{
		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/Flaky.Api", "panoramicdata/Renamed.Api"),
			Listed(),
			(fullName, _) => fullName.EndsWith("Flaky.Api", StringComparison.Ordinal)
				? throw new HttpRequestException("rate limited")
				: Task.FromResult<ResolvedRepository?>(new ResolvedRepository("panoramicdata/Renamed.Api.Archived", true, false)));

		resolved.Select(info => info.FullName).Should().Equal("panoramicdata/Renamed.Api");
	}

	[Fact]
	public async Task ARenamedRepositoryThatIsStillActive_IsReportedNotArchived()
	{
		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/Old.Name"),
			Listed(),
			(_, _) => Task.FromResult<ResolvedRepository?>(new ResolvedRepository("panoramicdata/New.Name", false, false)));

		resolved.Should().ContainSingle().Which.IsArchived.Should().BeFalse(
			"a rename alone is no reason to stop governing a repository");
	}

	[Fact]
	public async Task ARepositoryNowOwnedBySomeoneElse_IsNotDescribedAsThisOrganisations()
	{
		var resolved = await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/Moved.Api"),
			Listed(),
			(_, _) => Task.FromResult<ResolvedRepository?>(new ResolvedRepository("someone-else/Moved.Api", IsArchived: true, IsFork: false)));

		resolved.Should().BeEmpty(
			"a repository that now belongs to another owner is not this organisation's to describe, and reporting "
				+ "it would attach another owner's archived state to a row governed here");
	}

	[Fact]
	public async Task TheLookupIsGivenTheOwnerAndNameAsRecorded()
	{
		var asked = new List<string>();

		await ResolveAsync(
			"panoramicdata",
			Published("panoramicdata/PanoramicData.DocMagic"),
			Listed(),
			(fullName, _) =>
			{
				asked.Add(fullName);
				return Task.FromResult<ResolvedRepository?>(null);
			});

		asked.Should().Equal("panoramicdata/PanoramicData.DocMagic");
	}
}
