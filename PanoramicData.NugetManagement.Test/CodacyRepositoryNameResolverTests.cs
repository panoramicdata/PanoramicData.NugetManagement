using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="CodacyRepositoryNameResolver"/>, which recovers the name Codacy holds for a
/// repository that has since been renamed on the provider.
/// </summary>
public class CodacyRepositoryNameResolverTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task ReturnsCodacyName_WhenOnlyCasingDiffers()
	{
		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			Listing(["Meraki.Api", "Dell.CloudIq.Api", "Auvik.Api"]),
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().Be("Dell.CloudIq.Api");
	}

	[Fact]
	public async Task ReturnsNull_WhenTheExactNameIsListed()
	{
		// The 404 was not about casing, so retrying under another spelling would only hide the cause.
		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			Listing(["Dell.CloudIQ.Api"]),
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().BeNull();
	}

	[Fact]
	public async Task ReturnsNull_WhenTheRepositoryIsAbsent()
	{
		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			Listing(["Meraki.Api", "Auvik.Api"]),
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().BeNull();
	}

	[Fact]
	public async Task ReadsEveryPage_WhenTheMatchIsBeyondTheFirst()
	{
		var pages = new Queue<CodacyRepositoryNamePage>(
		[
			new() { Names = ["Meraki.Api"], Cursor = "page2" },
			new() { Names = ["Auvik.Api"], Cursor = "page3" },
			new() { Names = ["Dell.CloudIq.Api"], Cursor = null }
		]);

		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			(_, _) => Task.FromResult(pages.Dequeue()),
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().Be("Dell.CloudIq.Api");
		pages.Should().BeEmpty();
	}

	[Fact]
	public async Task StopsPaging_WhenTheMatchIsFound()
	{
		var calls = 0;

		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			(_, _) =>
			{
				calls++;
				return Task.FromResult(new CodacyRepositoryNamePage
				{
					Names = ["Dell.CloudIq.Api"],
					Cursor = "there-is-more"
				});
			},
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().Be("Dell.CloudIq.Api");
		calls.Should().Be(1);
	}

	[Fact]
	public async Task IgnoresNamelessEntries()
	{
		var resolved = await CodacyRepositoryNameResolver.ResolveAsync(
			(_, _) => Task.FromResult(new CodacyRepositoryNamePage
			{
				Names = [null, "Dell.CloudIq.Api"],
				Cursor = null
			}),
			"Dell.CloudIQ.Api",
			TestContext.Current.CancellationToken);

		resolved.Should().Be("Dell.CloudIq.Api");
	}

	private static Func<string?, CancellationToken, Task<CodacyRepositoryNamePage>> Listing(string?[] names)
		=> (_, _) => Task.FromResult(new CodacyRepositoryNamePage { Names = names, Cursor = null });
}
