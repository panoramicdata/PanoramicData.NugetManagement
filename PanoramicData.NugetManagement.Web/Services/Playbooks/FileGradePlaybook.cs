using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// CQ-06: raise one file's Codacy grade, usually by removing duplication from a test file.
/// </summary>
/// <remarks>
/// Written from the Uk.Parliament session logs. Sessions read thirteen to sixteen files before their first
/// edit when the named file was enough, guessed the return type of an extracted helper so the first build
/// failed, and rewrote much more of the file than the repeated block.
/// </remarks>
public sealed class FileGradePlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "CQ-06";

	/// <inheritdoc />
	public string Goal
		=> "Improve this file's Codacy grade by fixing what the facts say is wrong with it.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files => ["the one file named in the task"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "The project still builds. Every test still exists with the same name and checks the same thing: "
			+ "none was deleted, skipped, merged or weakened. The repeated code now exists once. You changed "
			+ "only the file you were told to change, and only the parts of it the problem is in.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			How to work:
			1. Read the file you were told to change. Read another file only to see a type this one uses.
			2. Change one repeated block at a time with replace_in_file, then call run_build.
			   Fix any compile error before the next change.
			3. Copy a type exactly from where it is declared. Do not guess it.

			Shape of the change, for repeated setup in tests:

			  Before: three tests each contain the same four lines that create a client and call it.
			  After: one private method holds those four lines and returns the result;
			         each test calls the method and keeps its own assertions.

			Shape of the change, when the task says the same code is in other files:

			  Before: four interfaces each declare the same two methods.
			  After: one new interface in the same folder declares them;
			         each of the four inherits from it and keeps only what is its own.

			Joining lines, moving braces or renaming does not remove a repeat.
			""";
}
