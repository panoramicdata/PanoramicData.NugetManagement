using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DeliveryOnlyWork"/>: the work a delivery-only pipeline must never be asked for.
/// </summary>
public class DeliveryOnlyWorkTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly AiChangeRequest Request = new("o", "r", AiChangeKind.RuleFix, "rule-x-a", "Fix X", null, false);

	[Fact]
	public async Task WriteRedTestAsync_Throws()
	{
		var act = () => new DeliveryOnlyWork().WriteRedTestAsync(Request, null, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<InvalidOperationException>(
			"a pipeline built only to deliver finished edits has no model to ask for a test");
	}

	[Fact]
	public async Task FixAsync_Throws()
	{
		var act = () => new DeliveryOnlyWork().FixAsync(Request, null, TestContext.Current.CancellationToken);

		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
