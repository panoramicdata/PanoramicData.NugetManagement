using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="DocBlockValidator"/>: the quality gate on what the model proposes.</summary>
public class DocBlockValidatorTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static DocSlot Slot(string member = "Customer.Name", string declaration = "public string Name { get; set; }")
		=> new(1, 0, 0, "\t", [member], declaration);

	private static string[] Summary(params string[] body)
		=> ["/// <summary>", .. body.Select(b => "/// " + b), "/// </summary>"];

	[Fact]
	public void AcceptsARealSummary()
		=> DocBlockValidator.Reject(Summary("The legal name the customer trades under."), Slot()).Should().BeNull();

	[Fact]
	public void RejectsEmpty()
		=> DocBlockValidator.Reject([], Slot()).Should().NotBeNull();

	[Fact]
	public void RejectsAnEmptySummary()
		=> DocBlockValidator.Reject(["/// <summary>", "/// </summary>"], Slot()).Should().Contain("empty");

	[Fact]
	public void RejectsMalformedXml()
		=> DocBlockValidator.Reject(Summary("Uses <b> without closing."), Slot()).Should().Contain("XML");

	[Fact]
	public void RejectsALineThatIsNotADocLine()
		=> DocBlockValidator.Reject(["/// <summary>", "text", "/// </summary>"], Slot()).Should().NotBeNull();

	[Fact]
	public void RejectsAMissingSummary()
		=> DocBlockValidator.Reject(["/// <returns>Something.</returns>"], Slot()).Should().Contain("summary");

	[Fact]
	public void RejectsMultiParagraphSummaries()
	{
		DocBlockValidator.Reject(Summary("First paragraph.", "", "Second paragraph."), Slot()).Should().Contain("paragraph");
		DocBlockValidator.Reject(Summary("<para>One.</para><para>Two.</para>"), Slot()).Should().Contain("paragraph");
	}

	[Theory]
	[InlineData("Call `Foo()` first.")]
	[InlineData("Sets x = 1; then returns.")]
	[InlineData("Used as { get; set; } here.")]
	[InlineData("Equivalent to x => x.")]
	[InlineData("<code>var x = 1;</code>")]
	public void RejectsCode(string text)
		=> DocBlockValidator.Reject(Summary(text), Slot()).Should().NotBeNull();

	[Theory]
	[InlineData("Gets or sets the Name.")]
	[InlineData("The name.")]
	[InlineData("Gets or sets the")]
	public void RejectsCosmeticOnlyText(string text)
		=> DocBlockValidator.Reject(Summary(text), Slot()).Should().Contain("restates");

	[Fact]
	public void AcceptsTextThatSaysSomethingBeyondTheName()
		=> DocBlockValidator.Reject(Summary("Gets or sets the name shown on invoices."), Slot()).Should().BeNull();

	[Fact]
	public void RejectsConstructorBoilerplate()
		=> DocBlockValidator.Reject(Summary("Initializes a new instance of the Customer class."), Slot("Customer.Customer()", "public Customer()"))
			.Should().Contain("restates");

	[Theory]
	[InlineData("DateTime?")]
	[InlineData("List[]")]
	public void RejectsInvalidCrefSyntax(string cref)
		=> DocBlockValidator.Reject(["/// <summary>", $"/// See <see cref=\"{cref}\"/> for the billing date.", "/// </summary>"], Slot())
			.Should().Contain("cref");

	[Fact]
	public void AcceptsAGenericCref()
		=> DocBlockValidator.Reject(Summary("Wraps a <see cref=\"List{T}\"/> of billing dates."), Slot()).Should().BeNull();

	[Fact]
	public void RejectsAParamTagForAnUnknownParameter()
		=> DocBlockValidator.Reject(
			["/// <summary>", "/// Sends the invoice by post.", "/// </summary>", "/// <param name=\"ghost\">Nobody.</param>"],
			Slot("Customer.Send(int)", "public void Send(int count)"))
			.Should().Contain("ghost");

	[Fact]
	public void AcceptsAParamTagForARealParameter()
		=> DocBlockValidator.Reject(
			["/// <summary>", "/// Sends the invoice by post.", "/// </summary>", "/// <param name=\"count\">How many copies to send.</param>"],
			Slot("Customer.Send(int)", "public void Send(int count)"))
			.Should().BeNull();

	[Fact]
	public void RejectsAnUnexpectedElement()
		=> DocBlockValidator.Reject(["/// <summary>", "/// Does a thing for billing.", "/// </summary>", "/// <script>x</script>"], Slot())
			.Should().Contain("script");

	[Fact]
	public void RejectsAnOverlongSummary()
		=> DocBlockValidator.Reject(Summary(string.Join(' ', Enumerable.Repeat("word", 130))), Slot())
			.Should().Contain("long");
}
