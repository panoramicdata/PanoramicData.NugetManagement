using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Whether a C# file declares things and does nothing: interfaces, enums, records and data classes, with
/// no method, accessor or constructor body to execute.
/// </summary>
/// <remarks>
/// Written for the changed-line coverage gate. A change to such a file has no logic for a test to exercise,
/// yet the gate used to refuse it: an interface or a data class either never appears in the coverage report,
/// or appears only because a source generator such as Refit's attributes its generated code to the interface
/// file's lines, which no unit test runs. Refusing every refactor of declarative code meant most duplication
/// fixes in an API client could never be delivered. The build, the existing tests and CI still judge such a
/// change; only the coverage arithmetic is spared.
/// <para>
/// Deliberately conservative: anything that could be code is treated as code, so a doubtful file stays
/// subject to the gate. A property initialiser such as <c>= []</c> is allowed — it is part of nearly every
/// data class and is exercised the moment one is created.
/// </para>
/// </remarks>
public static class DeclarationOnlySource
{
	private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);

	// Comments, then string and character literals, which can all contain text that looks like code.
	private static readonly Regex _commentsAndLiterals = new(
		"""//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|\$?"(?:[^"\\\n]|\\.)*"|'(?:[^'\\]|\\.)*'""",
		RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
		_timeout);

	// A parameter list or base-constructor call closing straight into a brace: the opening of a method,
	// constructor, local function or control statement body.
	private static readonly Regex _bodyAfterParameters = new(
		@"\)\s*(?::\s*(?:base|this)\s*\([^)]*\)\s*)?\{",
		RegexOptions.CultureInvariant | RegexOptions.Compiled,
		_timeout);

	// An accessor with a block body; the automatic form, "get;", has no brace after it.
	private static readonly Regex _accessorBody = new(
		@"\b(?:get|set|init)\s*\{",
		RegexOptions.CultureInvariant | RegexOptions.Compiled,
		_timeout);

	// Words that only occur inside something that executes.
	private static readonly Regex _statement = new(
		@"\b(?:return|throw|yield|await|foreach|while|for|if|else|switch|try|catch|finally|lock|do)\b|=>",
		RegexOptions.CultureInvariant | RegexOptions.Compiled,
		_timeout);

	/// <summary>
	/// Whether the source holds declarations only.
	/// </summary>
	/// <param name="source">The file's text.</param>
	public static bool Is(string source)
	{
		try
		{
			var code = _commentsAndLiterals.Replace(source, " ");

			return !_bodyAfterParameters.IsMatch(code)
				&& !_accessorBody.IsMatch(code)
				&& !_statement.IsMatch(code);
		}
		catch (RegexMatchTimeoutException)
		{
			// Too tangled to judge is not declarations only.
			return false;
		}
	}
}
