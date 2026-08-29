using System.Text.Json;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Test;

/// <summary>THROWAWAY probe — delete.</summary>
public class ZzTempCodacyProbe(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public async Task Probe()
	{
		var secretsPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"Microsoft", "UserSecrets", "PanoramicData.NugetManagement.Web", "secrets.json");
		var token = JsonDocument.Parse(File.ReadAllText(secretsPath))
			.RootElement.GetProperty("AppSettings:CodacyApiToken").GetString()!;

		var context = new RepositoryContext
		{
			FullName = "panoramicdata/Athonet.Api",
			Name = "Athonet.Api",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions { Codacy = new CodacyOptions { ApiToken = token } },
			FilePaths = [],
			FileContents = []
		};

		var configured = await new CodacyConfiguredRule().EvaluateAsync(context, TestContext.Current.CancellationToken);
		var grades = await new CodacyFileGradesRule().EvaluateAsync(context, TestContext.Current.CancellationToken);

		var missing = new RepositoryContext
		{
			FullName = "panoramicdata/ThisRepositoryDoesNotExistInCodacy",
			Name = "ThisRepositoryDoesNotExistInCodacy",
			DefaultBranch = "main",
			CurrentBranch = "main",
			Options = new RepoOptions { Codacy = new CodacyOptions { ApiToken = token } },
			FilePaths = [],
			FileContents = []
		};
		string raw;
		try
		{
			await new PanoramicData.NugetManagement.Services.CodacyFileGradeService()
				.GetGradesAsync(token, "panoramicdata", "ThisRepositoryDoesNotExistInCodacy", "main", TestContext.Current.CancellationToken);
			raw = "no exception";
		}
		catch (Exception rawEx)
		{
			var props = string.Join(", ", rawEx.GetType().GetProperties().Select(pr => $"{pr.Name}={pr.GetValue(rawEx)}"));
			raw = $"{rawEx.GetType().FullName} :: base={rawEx.GetType().BaseType?.FullName} :: {props}";
		}

		var untracked = await new CodacyConfiguredRule().EvaluateAsync(missing, TestContext.Current.CancellationToken);

		Assert.Fail($"""
			CQ-03 passed={configured.Passed} applicable={configured.IsApplicable} sev={configured.Severity}
			  {configured.Message}
			CQ-06 passed={grades.Passed} applicable={grades.IsApplicable} sev={grades.Severity}
			  {grades.Message}
			  detail:
			{grades.Advisory?.Detail}
			RAW: {raw}
			CQ-03 (untracked repo) passed={untracked.Passed} applicable={untracked.IsApplicable}
			  {untracked.Message}
			""");
	}
}
