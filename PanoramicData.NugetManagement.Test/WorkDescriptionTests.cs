using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the sentence a work item's pane leads with.
/// </summary>
/// <remarks>
/// The pane exists to say what the work is, and "FixWithAiRule" is a symbol rather than an
/// explanation. Everything a kind carries in its descriptor — which rule, which category — has to
/// reach that sentence, or the pane says less than the tree node above it.
/// </remarks>
public class WorkDescriptionTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void EveryKind_HasASentence()
	{
		// A kind with no sentence falls back to its enum name, which is exactly what the pane is for
		// avoiding. This is the test that stops a new WorkKind shipping without one.
		var missing = Enum.GetValues<WorkKind>()
			.Where(kind => !WorkDescription.IsDescribed(kind))
			.ToList();

		missing.Should().BeEmpty("a work item whose pane cannot say what it is has no reason to exist");
	}

	[Fact]
	public void AiFix_NamesTheRuleItIsFixing()
	{
		var item = Item(WorkDescriptor.ForRepository(
			WorkKind.FixWithAiRule,
			"panoramicdata",
			"panoramicdata/Athonet.Api",
			("ruleId", "META-04")));

		WorkDescription.For(item).Should().Contain("META-04")
			.And.Contain("panoramicdata/Athonet.Api");
	}

	[Fact]
	public void AiFix_SaysAModelIsDoingIt()
	{
		var item = Item(WorkDescriptor.ForRepository(
			WorkKind.FixWithAiRule,
			"panoramicdata",
			"panoramicdata/Athonet.Api",
			("ruleId", "META-04")));

		// The distinguishing fact about this kind: it may take minutes, and what appears below is a
		// model's session rather than a command's output.
		WorkDescription.For(item).Should().Contain("model", Exactly.Once());
	}

	[Fact]
	public void CategoryFix_NamesTheCategory()
	{
		var item = Item(WorkDescriptor.ForRepository(
			WorkKind.FixCategory,
			"panoramicdata",
			"panoramicdata/Athonet.Api",
			("category", "ProjectMetadata")));

		WorkDescription.For(item).Should().Contain("ProjectMetadata");
	}

	[Fact]
	public void OrganisationWork_NamesTheOrganisationRatherThanARepository()
	{
		var item = Item(WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata"));

		WorkDescription.For(item).Should().Contain("panoramicdata");
	}

	[Fact]
	public void IsAiWork_IsTrueOnlyForTheModelBackedKind()
	{
		WorkDescription.IsAi(WorkKind.FixWithAiRule).Should().BeTrue();
		WorkDescription.IsAi(WorkKind.FixRule).Should().BeFalse(
			"a deterministic remediation is not a model session, and the pane frames the two differently");
	}

	private static WorkItem Item(WorkDescriptor descriptor) => new()
	{
		Id = "1",
		Title = "Work",
		Descriptor = descriptor,
		DedupKey = "k"
	};
}
