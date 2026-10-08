#:project C:\Users\david\source\repos\panoramicdata\PanoramicData.NugetManagement\PanoramicData.NugetManagement\PanoramicData.NugetManagement.csproj
#:package Microsoft.Extensions.Configuration.UserSecrets@10.0.12
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Assesses one GitHub repository against every NugetManagement rule and prints PASS/FAIL per rule.
// Copy this file to your scratchpad first (run inside this repository it would inherit Directory.Packages.props), then:
//   dotnet run Assess.cs -- panoramicdata/Splunk.Api | Select-String -NotMatch '^\[PASS\]'
// Tokens come from existing user secrets: GitHub:Token (the NugetManagement.Test project) and
// AppSettings:CodacyApiToken (the Web project). Neither is printed. The two properties above re-enable reflection JSON,
// which file-based apps disable; without them Codacy.Api fails and every Codacy-backed rule silently passes.
// The rules are this repository's current RuleRegistry, so a rule added since the last run applies immediately.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;
using PanoramicData.NugetManagement.Services;

var fullName = args.Length > 0 ? args[0] : "panoramicdata/Splunk.Api";
var parts = fullName.Split('/');

var testSecrets = new ConfigurationBuilder().AddUserSecrets("e63e60d9-8d7c-4e1e-b5c2-f1a5e3b7f4a2").Build();
var webSecrets = new ConfigurationBuilder().AddUserSecrets("PanoramicData.NugetManagement.Web").Build();
var githubToken = testSecrets["GitHub:Token"] ?? throw new InvalidOperationException("GitHub:Token missing");
var codacyToken = webSecrets["AppSettings:CodacyApiToken"];

var github = new GitHubClient(new ProductHeaderValue("Splunk.Api.Assess")) { Credentials = new Credentials(githubToken) };
var builder = new RepositoryContextBuilder(github, NullLogger<RepositoryContextBuilder>.Instance);
var repository = await github.Repository.Get(parts[0], parts[1]);
var options = new RepoOptions { Codacy = string.IsNullOrWhiteSpace(codacyToken) ? null : new CodacyOptions { ApiToken = codacyToken } };
var context = await builder.BuildAsync(repository, options, CancellationToken.None);

var passed = 0;
var failed = new List<string>();
foreach (var rule in RuleRegistry.Rules)
{
	RuleResult result;
	try
	{
		result = await rule.EvaluateAsync(context, CancellationToken.None);
	}
	catch (Exception ex)
	{
		failed.Add($"[EXCEPTION] {rule.GetType().Name}: {ex.GetType().Name} {ex.Message}");
		continue;
	}

	if (result.Passed)
	{
		passed++;
		Console.WriteLine($"[PASS] {result.RuleId}: {result.Message}");
	}
	else
	{
		failed.Add($"[FAIL] {result.RuleId} ({result.Severity}): {result.Message}");
	}
}

Console.WriteLine();
foreach (var line in failed)
{
	Console.WriteLine(line);
}

Console.WriteLine($"\n{fullName}: {passed} passed, {failed.Count} failed (Codacy token {(codacyToken is null ? "absent" : "present")})");
