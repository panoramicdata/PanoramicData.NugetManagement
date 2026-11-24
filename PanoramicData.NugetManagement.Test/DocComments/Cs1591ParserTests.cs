using PanoramicData.NugetManagement.Web.Services.DocComments;

namespace PanoramicData.NugetManagement.Test.DocComments;

/// <summary>Tests for <see cref="Cs1591Parser"/>: reading missing-comment diagnostics out of MSBuild output.</summary>
public class Cs1591ParserTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void ReadsAWarningLine()
	{
		const string log = @"C:\src\Foo.cs(12,17): warning CS1591: Missing XML comment for publicly visible type or member 'Foo.Bar'";

		var result = Cs1591Parser.Parse(log);

		result.Should().ContainSingle().Which.Should().Be(new MissingDocDiagnostic(@"C:\src\Foo.cs", 12, 17, "Foo.Bar"));
	}

	[Fact]
	public void ReadsParallelNodePrefixAndErrorSeverity()
	{
		const string log = @"   3>C:\src\Foo.cs(5,3): error CS1591: Missing XML comment for publicly visible type or member 'Foo' [C:\src\Foo.csproj]";

		Cs1591Parser.Parse(log).Should().ContainSingle().Which.Line.Should().Be(5);
	}

	[Fact]
	public void DoesNotSwallowTheTailOfAWrappedPrecedingLine()
	{
		const string log = "      some wrapped text that ends mid-path C:\\a\\b\r\nC:\\src\\Real.cs(1,1): warning CS1591: Missing XML comment for publicly visible type or member 'Real'";

		Cs1591Parser.Parse(log).Should().ContainSingle().Which.FilePath.Should().Be(@"C:\src\Real.cs");
	}

	[Fact]
	public void CollapsesRepeatsFromMultipleTargetFrameworks()
	{
		const string line = @"C:\src\Foo.cs(2,2): warning CS1591: Missing XML comment for publicly visible type or member 'Foo'";

		Cs1591Parser.Parse(line + "\r\n" + line).Should().ContainSingle();
	}

	[Fact]
	public void IgnoresOtherDiagnostics()
	{
		const string log = @"C:\src\Foo.cs(2,2): warning CS1573: Parameter 'x' has no matching param tag in the XML comment";

		Cs1591Parser.Parse(log).Should().BeEmpty();
	}

	[Fact]
	public void EmptyOutputGivesNothing() => Cs1591Parser.Parse(string.Empty).Should().BeEmpty();
}
