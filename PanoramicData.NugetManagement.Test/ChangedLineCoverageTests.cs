using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="ChangedLineCoverage"/>: whether the lines a change touched are exercised.
/// </summary>
public class ChangedLineCoverageTests(ITestOutputHelper output) : TestWithOutput(output)
{
	private const string Cobertura = """
		<coverage>
		  <packages><package><classes>
		    <class filename="C:\src\repo\Lib\Parser.cs">
		      <lines>
		        <line number="10" hits="4" />
		        <line number="11" hits="0" />
		        <line number="12" hits="2" />
		      </lines>
		    </class>
		  </classes></package></packages>
		</coverage>
		""";

	private static ChangedFileLines Changed(string path, params int[] lines) => new(path, lines.ToHashSet());

	[Fact]
	public void Evaluate_PassesWhenEnoughChangedExecutableLinesRan()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 10, 12)], Cobertura);

		result.Passed.Should().BeTrue();
		result.Covered.Should().Be(2);
		result.Executable.Should().Be(2);
	}

	[Fact]
	public void Evaluate_FailsBelowTheThreshold()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 10, 11)], Cobertura);

		result.Passed.Should().BeFalse("one of two executable changed lines is 50%, under the 60% bar");
		result.Message.Should().Contain("Lib/Parser.cs");
	}

	[Fact]
	public void Evaluate_IgnoresChangedLinesThatAreNotExecutable()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 1, 2, 12)], Cobertura);

		result.Executable.Should().Be(1, "lines 1 and 2 are comments or usings and have no coverage entry");
		result.Passed.Should().BeTrue();
	}

	[Fact]
	public void Evaluate_PassesWhenNoChangedLineIsExecutable()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 1)], Cobertura);

		result.Passed.Should().BeTrue("a change with nothing to execute has nothing to leave uncovered");
		result.Executable.Should().Be(0);
	}

	[Fact]
	public void Evaluate_ReadsTheShapeTheRealCollectorWrites_WithoutCountingMethodLinesTwice()
	{
		const string real = """
			<coverage><packages><package><classes>
			  <class name="Lib.Parser" filename="C:\r\Lib\Parser.cs">
			    <methods>
			      <method name="Parse"><lines>
			        <line number="5" hits="1" branch="False" />
			        <line number="6" hits="0" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line>
			      </lines></method>
			    </methods>
			    <lines>
			      <line number="5" hits="1" branch="False" />
			      <line number="6" hits="0" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="0" type="jump" coverage="50%" /></conditions></line>
			    </lines>
			  </class>
			</classes></package></packages></coverage>
			""";

		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Parser.cs", 5, 6)], real);

		result.Executable.Should().Be(2, "the method's lines are repeated at class level, and counting both would double them");
		result.Covered.Should().Be(1);
		result.Passed.Should().BeFalse("one of two is 50%, under the 60% bar");
	}

	[Fact]
	public void Evaluate_TreatsAChangedFileMissingFromTheReportAsUncovered()
	{
		var result = ChangedLineCoverage.Evaluate([Changed("Lib/Other.cs", 5)], Cobertura);

		result.Passed.Should().BeFalse(
			"a changed code file the coverage run never saw has not been exercised, and passing it would "
				+ "let any unreferenced file through");
	}
}
