using System.Text;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DuplicationMeasurement"/>, the local stand-in for Codacy's duplication grade. What
/// matters most is that a cosmetic edit scores the same as no edit: that is the failure it exists to catch.
/// </summary>
public class DuplicationMeasurementTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"dup-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	/// <summary>Enough statements to be well past the 50-token threshold.</summary>
	private static string SharedBody(string indent = "\t\t")
	{
		var builder = new StringBuilder();

		for (var i = 0; i < 10; i++)
		{
			builder.AppendLine($"{indent}var value{i} = Compute({i}, \"label{i}\", options.Enabled);");
		}

		return builder.ToString();
	}

	private static string Source(string typeName, string body)
		=> $$"""
			using System;
			using System.Linq;

			namespace Sample;

			public class {{typeName}}
			{
				public void Run()
				{
			{{body}}
				}
			}
			""";

	private void Write(string relativePath, string content)
	{
		var full = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	[Fact]
	public void ARunThatAppearsInAnotherFile_IsReportedWithWhereElseItIs()
	{
		Write("A.cs", Source("A", SharedBody()));
		Write("B.cs", Source("B", SharedBody()));
		Write("C.cs", Source("C", "\t\tConsole.WriteLine(\"unrelated\");"));

		var report = DuplicationMeasurement.Measure(_root, "A.cs");

		report.Should().NotBeNull();
		report!.Percent.Should().BeGreaterThan(50);
		report.PeerFiles.Should().BeEquivalentTo("B.cs");
		report.Blocks.Should().NotBeEmpty();
	}

	[Fact]
	public void RunsThatDifferOnlyInTheirStringLiterals_AreStillRepeats()
	{
		// The shape of Aruba.Api's four troubleshooting interfaces: the same declarations, a different route.
		string Declarations(string route) => string.Concat(Enumerable.Range(0, 6).Select(i =>
			$"\t[Post(\"/{route}/{{serialNumber}}/op{i}\")]\n\tTask<Response> Op{i}Async(string serialNumber, CancellationToken cancellationToken = default);\n"));

		Write("IA.cs", $"public interface IA\n{{\n{Declarations("aps")}}}\n");
		Write("IB.cs", $"public interface IB\n{{\n{Declarations("cx")}}}\n");

		DuplicationMeasurement.Measure(_root, "IA.cs")!.Percent.Should().BeGreaterThan(80);
	}

	[Fact]
	public void AUniqueFile_ScoresZero()
	{
		Write("A.cs", Source("A", SharedBody()));
		Write("C.cs", Source("C", "\t\tConsole.WriteLine(\"unrelated\");"));

		DuplicationMeasurement.Measure(_root, "C.cs")!.Percent.Should().Be(0);
	}

	[Fact]
	public void ReformattingTheRepeat_DoesNotChangeTheScore()
	{
		Write("B.cs", Source("B", SharedBody()));
		Write("A.cs", Source("A", SharedBody()));
		var before = DuplicationMeasurement.Measure(_root, "A.cs")!;

		// Every statement joined onto one line, comments added, and a different indent: what a model does when
		// told to "remove duplication" and shown only a build.
		var joined = string.Join(' ', SharedBody().Split('\n').Select(line => line.Trim()));
		Write("A.cs", Source("A", $"// tidied\n\t\t{joined}"));

		var after = DuplicationMeasurement.Measure(_root, "A.cs")!;

		after.Percent.Should().BeApproximately(before.Percent, 1);
		DuplicationMeasurement.IsMeaningfulImprovement(before, after).Should().BeFalse();
	}

	[Fact]
	public void MovingTheRepeatIntoOnePlace_IsAMeaningfulImprovement()
	{
		Write("B.cs", Source("B", SharedBody()));
		Write("A.cs", Source("A", SharedBody()));
		var before = DuplicationMeasurement.Measure(_root, "A.cs")!;

		Write("Shared.cs", Source("Shared", SharedBody()));
		Write("A.cs", Source("A", "\t\tnew Shared().Run();"));

		var after = DuplicationMeasurement.Measure(_root, "A.cs")!;

		after.Percent.Should().BeLessThan(10);
		DuplicationMeasurement.IsMeaningfulImprovement(before, after).Should().BeTrue();
	}

	[Fact]
	public void UsingDirectivesAndNamespaces_DoNotCountAsRepeats()
	{
		Write("A.cs", "using System;\nusing System.Linq;\nusing System.Text;\nusing System.IO;\nnamespace Sample;\npublic class A { }\n");
		Write("B.cs", "using System;\nusing System.Linq;\nusing System.Text;\nusing System.IO;\nnamespace Sample;\npublic class B { }\n");

		DuplicationMeasurement.Measure(_root, "A.cs")!.Percent.Should().Be(0);
	}

	[Fact]
	public void BuildOutputFolders_AreNotPeers()
	{
		Write("A.cs", Source("A", SharedBody()));
		Write("obj/Debug/B.cs", Source("B", SharedBody()));
		Write("bin/B.cs", Source("B", SharedBody()));

		DuplicationMeasurement.Measure(_root, "A.cs")!.Percent.Should().Be(0);
	}

	[Fact]
	public void AFileThatIsNotCSharpOrDoesNotExist_ReturnsNull()
	{
		Write("notes.md", "text");

		DuplicationMeasurement.Measure(_root, "notes.md").Should().BeNull();
		DuplicationMeasurement.Measure(_root, "Missing.cs").Should().BeNull();
	}

	[Fact]
	public void Describe_NamesTheRunsAndWhereElseTheyAre()
	{
		Write("A.cs", Source("A", SharedBody()));
		Write("B.cs", Source("B", SharedBody()));

		var text = DuplicationMeasurement.Describe(DuplicationMeasurement.Measure(_root, "A.cs")!);

		text.Should().Contain("also appear in B.cs");
	}

	[Theory]
	[InlineData(80, 55, true)]
	[InlineData(80, 70, false)]
	[InlineData(40, 9, true)]
	[InlineData(12, 11, false)]
	public void IsMeaningfulImprovement_NeedsAQuarterOffOrUnderTenPercent(double before, double after, bool expected)
		=> DuplicationMeasurement
			.IsMeaningfulImprovement(new DuplicationReport(before, 0, 0, []), new DuplicationReport(after, 0, 0, []))
			.Should().Be(expected);
}
