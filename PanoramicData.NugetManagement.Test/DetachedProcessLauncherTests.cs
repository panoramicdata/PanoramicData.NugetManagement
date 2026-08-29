using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Covers the command line quoting used when spawning IDEs detached from this app's job object.
/// </summary>
public class DetachedProcessLauncherTests
{
	[Fact]
	public void BuildCommandLine_WithNoArgument_QuotesExecutableOnly()
	{
		var commandLine = DetachedProcessLauncher.BuildCommandLine(@"C:\Program Files\Microsoft VS Code\Code.exe", null);

		commandLine.Should().Be(@"""C:\Program Files\Microsoft VS Code\Code.exe""");
	}

	[Fact]
	public void BuildCommandLine_WithPathArgument_QuotesBoth()
	{
		var commandLine = DetachedProcessLauncher.BuildCommandLine(@"C:\Tools\Code.exe", @"C:\repos\My Repo");

		commandLine.Should().Be(@"""C:\Tools\Code.exe"" ""C:\repos\My Repo""");
	}

	[Fact]
	public void BuildCommandLine_WithTrailingSeparator_DoesNotEscapeTheClosingQuote()
	{
		// A lone trailing backslash would escape the closing quote and swallow the next argument.
		var commandLine = DetachedProcessLauncher.BuildCommandLine(@"C:\Tools\Code.exe", @"C:\repos\My Repo\");

		commandLine.Should().Be(@"""C:\Tools\Code.exe"" ""C:\repos\My Repo\\""");
	}

	[Fact]
	public void BuildCommandLine_WithEmptyArgument_QuotesExecutableOnly()
	{
		var commandLine = DetachedProcessLauncher.BuildCommandLine(@"C:\Tools\Code.exe", string.Empty);

		commandLine.Should().Be(@"""C:\Tools\Code.exe""");
	}
}
