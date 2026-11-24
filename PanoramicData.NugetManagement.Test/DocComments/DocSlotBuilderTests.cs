using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="DocSlotBuilder"/>: where a doc comment must go so it binds to its member.</summary>
public class DocSlotBuilderTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static MissingDocDiagnostic At(int line, string member = "M") => new("f.cs", line, 1, member);

	[Fact]
	public void AMemberWithNoAttributeIsInsertedAtItsOwnLine()
	{
		var slots = DocSlotBuilder.Build("class A\n{\n\tpublic int B;\n}\n", [At(3)]);

		var slot = slots.Should().ContainSingle().Subject;
		slot.InsertLine.Should().Be(2);
		slot.Indent.Should().Be("\t");
	}

	[Fact]
	public void ClimbsAboveAMultiLineAttributeAndABlankLine()
	{
		const string text = "namespace N\n{\n\t[Spec(\n\t\tPath = \"a\",\n\t\tVerbs = \"b\")]\n\n\tpublic class C\n\t{\n\t}\n}\n";

		var slot = DocSlotBuilder.Build(text, [At(7)]).Single();

		slot.InsertLine.Should().Be(2, "the doc must sit above the attribute list, not between it and the type");
	}

	[Fact]
	public void ClimbsAboveStackedAttributesAndASlashSlashComment()
	{
		const string text = "{\n\t[A]\n\t[B] // note\n\t// NOSONAR\n\tpublic int X;\n}\n";

		DocSlotBuilder.Build(text, [At(5)]).Single().InsertLine.Should().Be(1);
	}

	[Fact]
	public void ATypeAndItsPrimaryConstructorShareOneSlot()
	{
		var slots = DocSlotBuilder.Build("public class P(int x)\n{\n}\n", [At(1, "P"), At(1, "P.P(int)")]);

		slots.Should().ContainSingle().Which.Members.Should().HaveCount(2);
	}

	[Fact]
	public void ALineOutsideTheFileIsDropped()
		=> DocSlotBuilder.Build("class A { }\n", [At(99)]).Should().BeEmpty();

	[Fact]
	public void SlotsAreNumberedFromOneInSourceOrder()
	{
		var slots = DocSlotBuilder.Build("{\n\tint a;\n\tint b;\n}\n", [At(3), At(2)]);

		slots.Select(s => s.Id).Should().Equal(1, 2);
		slots.Select(s => s.DeclLine).Should().Equal(1, 2);
	}
}
