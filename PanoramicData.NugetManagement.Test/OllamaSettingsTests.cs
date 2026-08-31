using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the Ollama configuration: what it defaults to, what it clamps, and when it is complete
/// enough to offer AI fixing at all.
/// </summary>
public class OllamaSettingsTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"ollama-settings-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private RuntimeSettingsService NewService()
	{
		Directory.CreateDirectory(_directory);

		return new RuntimeSettingsService(
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata" }),
			NullLogger<RuntimeSettingsService>.Instance,
			Path.Combine(_directory, "runtime-settings.json"));
	}

	[Fact]
	public void OutOfTheBox_ThereIsNoOllamaConfiguredAndAiFixingIsNotOffered()
	{
		var ollama = NewService().Ollama;

		ollama.BaseUrl.Should().BeNullOrWhiteSpace();
		ollama.IsConfigured.Should().BeFalse(
			"offering a button that cannot work is worse than not offering it");
	}

	[Fact]
	public void AUrlAndAModel_AreEnoughToBeConfigured()
	{
		var service = NewService();

		service.SetOllama(new OllamaOptions
		{
			BaseUrl = "http://pdl-rune-02.panoramicdata.com:11434",
			Model = "qwen3.8:27b"
		});

		service.Ollama.IsConfigured.Should().BeTrue("the key is optional; a local box needs none");
	}

	[Fact]
	public void AUrlWithNoModel_IsNotConfigured()
		=> new OllamaOptions { BaseUrl = "http://localhost:11434", Model = "  " }
			.IsConfigured.Should().BeFalse("there is no sensible default model to guess");

	[Fact]
	public void AModelWithNoUrl_IsNotConfigured()
		=> new OllamaOptions { BaseUrl = null, Model = "qwen3.8:27b" }
			.IsConfigured.Should().BeFalse();

	[Fact]
	public void AUrlThatIsNotAUrl_IsNotConfigured()
		=> new OllamaOptions { BaseUrl = "pdl-rune-02", Model = "qwen3.8:27b" }
			.IsConfigured.Should().BeFalse("a hostname with no scheme is a common mistake worth catching early");

	[Fact]
	public void TheDefaults_MatchWhatALocalBoxNeeds()
	{
		var ollama = new OllamaOptions();

		ollama.ContextWindow.Should().Be(131072);
		ollama.RequestTimeoutMs.Should().Be(300_000);
		ollama.MaxConcurrency.Should().Be(1, "one GX10 does not want twenty sessions at once");
		ollama.MaxTurnsPerAttempt.Should().BeGreaterThan(1);
		ollama.MaxAttemptsPerRule.Should().BeGreaterThan(1);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-3)]
	public void ConcurrencyBelowOne_IsClamped(int requested)
	{
		var service = NewService();

		service.SetOllama(new OllamaOptions
		{
			BaseUrl = "http://localhost:11434",
			Model = "qwen3.8:27b",
			MaxConcurrency = requested
		});

		service.Ollama.MaxConcurrency.Should().Be(1, "zero would queue AI work that never runs");
	}

	[Fact]
	public void TheSettingsSurviveARestart()
	{
		var path = Path.Combine(_directory, "runtime-settings.json");
		Directory.CreateDirectory(_directory);

		var first = new RuntimeSettingsService(
			Options.Create(new AppSettings()),
			NullLogger<RuntimeSettingsService>.Instance,
			path);

		first.SetOllama(new OllamaOptions
		{
			BaseUrl = "http://pdl-rune-02.panoramicdata.com:11434",
			Model = "qwen3.8:27b",
			ApiKey = "sk-secret",
			MaxConcurrency = 2
		});

		var second = new RuntimeSettingsService(
			Options.Create(new AppSettings()),
			NullLogger<RuntimeSettingsService>.Instance,
			path);

		second.Ollama.BaseUrl.Should().Be("http://pdl-rune-02.panoramicdata.com:11434");
		second.Ollama.Model.Should().Be("qwen3.8:27b");
		second.Ollama.ApiKey.Should().Be("sk-secret");
		second.Ollama.MaxConcurrency.Should().Be(2);
	}

	/// <summary>
	/// The key is stored in plain text, which was a deliberate choice — this test exists so that
	/// choice is visible and cannot be forgotten, not because plain text is desirable.
	/// </summary>
	[Fact]
	public void TheApiKeyIsStoredInPlainText_WhichIsAKnownTradeOff()
	{
		var path = Path.Combine(_directory, "runtime-settings.json");
		Directory.CreateDirectory(_directory);

		var service = new RuntimeSettingsService(
			Options.Create(new AppSettings()),
			NullLogger<RuntimeSettingsService>.Instance,
			path);

		service.SetOllama(new OllamaOptions
		{
			BaseUrl = "http://localhost:11434",
			Model = "qwen3.8:27b",
			ApiKey = "sk-plain-text-on-purpose"
		});

		File.ReadAllText(path).Should().Contain("sk-plain-text-on-purpose",
			"anyone with the settings file has the key; the UI says so");
	}
}
