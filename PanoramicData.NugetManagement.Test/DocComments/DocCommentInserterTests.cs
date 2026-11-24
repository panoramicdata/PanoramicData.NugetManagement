using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="DocCommentInserter"/>: mechanical insertion that never touches code.</summary>
public class DocCommentInserterTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private static readonly string[] _doc = ["/// <summary>", "/// The thing.", "/// </summary>"];

	private static Dictionary<int, IReadOnlyList<string>> Docs(params int[] ids)
		=> ids.ToDictionary(i => i, _ => (IReadOnlyList<string>)_doc);

	private static string Apply(string text, params int[] lines)
	{
		var slots = DocSlotBuilder.Build(text, lines.Select(l => new MissingDocDiagnostic("f.cs", l, 1, "M")));

		return DocCommentInserter.Insert(text, slots, Docs([.. slots.Select(s => s.Id)]));
	}

	[Fact]
	public void KeepsCrlfAndTabsAndAddsASeparatorLine()
	{
		const string text = "namespace X\r\n{\r\n\tpublic class Foo\r\n\t{\r\n\t\tpublic int A;\r\n\t\tpublic int B;\r\n\t}\r\n}\r\n";

		var result = Apply(text, 3, 5, 6);

		result.Should().Be(
			"namespace X\r\n{\r\n\t/// <summary>\r\n\t/// The thing.\r\n\t/// </summary>\r\n\tpublic class Foo\r\n\t{\r\n"
			+ "\t\t/// <summary>\r\n\t\t/// The thing.\r\n\t\t/// </summary>\r\n\t\tpublic int A;\r\n\r\n"
			+ "\t\t/// <summary>\r\n\t\t/// The thing.\r\n\t\t/// </summary>\r\n\t\tpublic int B;\r\n\t}\r\n}\r\n");
	}

	[Fact]
	public void KeepsLfFiles()
	{
		var result = Apply("{\n\tint a;\n}\n", 2);

		result.Should().NotContain("\r").And.Contain("\t/// <summary>\n");
	}

	[Fact]
	public void PlacesTheDocAboveAMultiLineAttribute()
	{
		var result = Apply("{\n\t[A(\n\t\t1)]\n\tpublic int X;\n}\n", 4);

		result.Should().StartWith("{\n\t/// <summary>");
		result.IndexOf("/// </summary>", StringComparison.Ordinal).Should().BeLessThan(result.IndexOf("[A(", StringComparison.Ordinal));
	}

	[Fact]
	public void AnUnansweredSlotIsLeftUndocumented()
	{
		const string text = "{\n\tint a;\n\tint b;\n}\n";
		var slots = DocSlotBuilder.Build(text, [new("f.cs", 2, 1, "a"), new("f.cs", 3, 1, "b")]);

		var result = DocCommentInserter.Insert(text, slots, Docs(slots[1].Id));

		result.Should().Contain("\tint a;").And.Contain("/// <summary>");
		result.Split("/// <summary>").Length.Should().Be(2, "only one slot was answered");
	}

	[Fact]
	public void StripAndCompareAcceptsOnlyDocsAndBlankLines()
	{
		const string original = "{\n\tint a;\n}\n";
		const string documented = "{\n\n\t/// <summary>\n\t/// x\n\t/// </summary>\n\tint a;\n}\n";

		DocCommentInserter.CodeUnchanged(original, documented).Should().BeTrue();
		DocCommentInserter.CodeUnchanged(original, documented.Replace("int a", "long a")).Should().BeFalse();
		DocCommentInserter.CodeUnchanged(original, "{\n}\n").Should().BeFalse("a deleted code line is a change");
	}
}
