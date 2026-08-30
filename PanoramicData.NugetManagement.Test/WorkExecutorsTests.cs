using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests that the catalogue of work and the code that runs it cannot drift apart.
/// </summary>
/// <remarks>
/// The bodies themselves are covered by the service tests they call into. What is not covered
/// anywhere else — and what a closed catalogue makes checkable at all — is that every kind has
/// somewhere to go.
/// </remarks>
public class WorkExecutorsTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void SupportedKinds_CoversEveryWorkKind()
	{
		var missing = Enum.GetValues<WorkKind>()
			.Where(kind => !WorkExecutors.SupportedKinds.Contains(kind))
			.ToList();

		missing.Should().BeEmpty(
			"a WorkKind with no executor would queue work that can never run");
	}

	[Fact]
	public void SupportedKinds_InventsNothing()
		=> WorkExecutors.SupportedKinds.Should().OnlyContain(kind => Enum.IsDefined(kind));
}
