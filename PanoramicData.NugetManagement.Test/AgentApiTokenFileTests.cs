using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="AgentApiTokenFile"/>: the API stays off unless someone switched it on, and a restart by
/// another session must not switch it off.
/// </summary>
public class AgentApiTokenFileTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"token-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string Write(string content)
	{
		Directory.CreateDirectory(_root);
		var path = Path.Combine(_root, "agent-token.txt");
		File.WriteAllText(path, content);
		return path;
	}

	[Fact]
	public void Read_WithNoFile_IsNull_AndDoesNotCreateOne()
	{
		var path = Path.Combine(_root, "agent-token.txt");

		AgentApiTokenFile.Read(path).Should().BeNull();
		File.Exists(path).Should().BeFalse("the API is off by default and reading must never switch it on");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   \r\n")]
	public void Read_WithNothingUsableInTheFile_IsNull(string content)
		=> AgentApiTokenFile.Read(Write(content)).Should().BeNull();

	[Fact]
	public void Read_TrimsTheToken()
		=> AgentApiTokenFile.Read(Write("abc123\r\n")).Should().Be("abc123");

	[Fact]
	public void Resolve_PrefersTheConfiguredTokenOverTheFile()
		=> AgentApiTokenFile.Resolve("configured", Write("from-file")).Should().Be("configured");

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void Resolve_FallsBackToTheFile_WhenNothingIsConfigured(string? configured)
		=> AgentApiTokenFile.Resolve(configured, Write("from-file")).Should().Be("from-file");
}
