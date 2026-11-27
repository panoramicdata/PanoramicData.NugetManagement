namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// The opt-in Codacy parity build: <c>dotnet build -p:CodacyParity=true</c> reports, as warnings, what Codacy's
/// SonarC# and Lizard engines would report, without changing normal builds or CI.
/// </summary>
/// <remarks>
/// Measured on LogicMonitor.Datamart: with these rules on and every other analyzer rule off, S2339 and S2360
/// matched Codacy exactly, CA1502 at 8 gave 11 hits against Lizard's 10, and MA0051 at 50 lines is stricter
/// than Lizard. Known gap: S1172 on unused parameters of interface implementations, which Codacy reports and
/// the analyzer does not. The rule list is the SonarC# patterns enabled in the organisation's default Codacy
/// coding standard; refresh it from the Codacy API when that standard changes.
/// </remarks>
public static class CodacyParityStandards
{
	private static readonly string[] _sonarRuleIds =
	[
		"S108",
		"S110",
		"S112",
		"S121",
		"S125",
		"S127",
		"S131",
		"S927",
		"S1006",
		"S1104",
		"S1116",
		"S1117",
		"S1118",
		"S1121",
		"S1163",
		"S1172",
		"S1185",
		"S1186",
		"S1206",
		"S1210",
		"S1215",
		"S1226",
		"S1244",
		"S1301",
		"S1313",
		"S1449",
		"S1450",
		"S1481",
		"S1643",
		"S1656",
		"S1698",
		"S1699",
		"S1764",
		"S1854",
		"S1858",
		"S1862",
		"S1871",
		"S1905",
		"S1940",
		"S1944",
		"S1994",
		"S2123",
		"S2156",
		"S2178",
		"S2184",
		"S2190",
		"S2197",
		"S2201",
		"S2223",
		"S2234",
		"S2259",
		"S2275",
		"S2291",
		"S2306",
		"S2326",
		"S2328",
		"S2330",
		"S2339",
		"S2345",
		"S2357",
		"S2360",
		"S2368",
		"S2372",
		"S2376",
		"S2386",
		"S2387",
		"S2437",
		"S2486",
		"S2551",
		"S2583",
		"S2674",
		"S2681",
		"S2688",
		"S2692",
		"S2696",
		"S2737",
		"S2743",
		"S2757",
		"S2760",
		"S2761",
		"S2930",
		"S2931",
		"S2933",
		"S2934",
		"S2953",
		"S2955",
		"S2995",
		"S2996",
		"S2997",
		"S3005",
		"S3010",
		"S3052",
		"S3168",
		"S3169",
		"S3172",
		"S3215",
		"S3216",
		"S3217",
		"S3218",
		"S3220",
		"S3234",
		"S3235",
		"S3236",
		"S3237",
		"S3241",
		"S3244",
		"S3246",
		"S3247",
		"S3249",
		"S3251",
		"S3253",
		"S3254",
		"S3257",
		"S3261",
		"S3262",
		"S3263",
		"S3264",
		"S3397",
		"S3427",
		"S3440",
		"S3441",
		"S3442",
		"S3443",
		"S3444",
		"S3445",
		"S3447",
		"S3449",
		"S3450",
		"S3451",
		"S3453",
		"S3456",
		"S3457",
		"S3458",
		"S3459",
		"S3466",
		"S3532",
		"S3597",
		"S3598",
		"S3600",
		"S3603",
		"S3610",
		"S3626",
		"S3655",
		"S3869",
		"S3871",
		"S3872",
		"S3874",
		"S3875",
		"S3876",
		"S3877",
		"S3880",
		"S3889",
		"S3897",
		"S3898",
		"S3926"
	];

	/// <summary>The MSBuild property that turns the parity build on.</summary>
	public static string Marker { get; } = "CodacyParity";

	/// <summary>Where the MSBuild targets live. MSBuild imports this file by name.</summary>
	public static string TargetsPath { get; } = "Directory.Build.targets";

	/// <summary>Where the generated analyzer configuration lives.</summary>
	public static string GlobalConfigPath { get; } = "build/codacy-parity.globalconfig";

	/// <summary>Where the cyclomatic complexity threshold lives.</summary>
	public static string CodeMetricsPath { get; } = "build/CodeMetricsConfig.txt";

	/// <summary>
	/// Lines that keep the two build files trackable when .gitignore ignores a <c>build</c> folder: git cannot re-include
	/// a file whose parent folder is ignored, so the folder is re-included and its other contents ignored again.
	/// </summary>
	public static IReadOnlyList<string> GitignoreReincludeLines { get; } =
	[
		"!/build/",
		"/build/*",
		"!/build/codacy-parity.globalconfig",
		"!/build/CodeMetricsConfig.txt"
	];

	/// <summary>True when .gitignore ignores a root <c>build</c> folder and does not yet re-include the parity files.</summary>
	public static bool BuildFolderIgnored(string? gitignore)
	{
		if (string.IsNullOrEmpty(gitignore))
		{
			return false;
		}

		var lines = gitignore.Split('\n').Select(line => line.Trim()).ToList();
		var ignored = false;
		foreach (var line in lines.Where(line => line.Length > 0 && line[0] != '#'))
		{
			var negated = line[0] == '!';
			var pattern = (negated ? line[1..] : line).TrimStart('/');
			pattern = pattern.EndsWith("/**", StringComparison.Ordinal) ? pattern[..^3] : pattern;
			pattern = pattern.EndsWith("/*", StringComparison.Ordinal) ? pattern[..^2] : pattern;
			if (pattern.TrimEnd('/') is "build" or "[Bb]uild" or "[bB]uild")
			{
				ignored = !negated;
			}
		}

		var reincluded = GitignoreReincludeLines.Skip(2).All(wanted => lines.Contains(wanted, StringComparer.Ordinal));
		return ignored && !reincluded;
	}

	/// <summary>The SonarC# rules the Codacy coding standard enables.</summary>
	public static IReadOnlyList<string> SonarRuleIds => _sonarRuleIds;

	/// <summary>The cyclomatic complexity Lizard allows per method before it reports.</summary>
	public static string CodeMetricsContent { get; } = "CA1502: 8\n";

	/// <summary>
	/// The MSBuild targets: off by default, and warnings only when on. The analyzer configuration is added from a
	/// target because the SDK builds its own list before this file is evaluated.
	/// </summary>
	public static string TargetsContent { get; } = """
		<Project>

			<!-- Opt-in Codacy parity: dotnet build -p:CodacyParity=true shows, as warnings, what Codacy would report.
			     Off by default, so normal builds and CI are unchanged. -->
			<PropertyGroup Condition="'$(CodacyParity)' == 'true'">
				<TreatWarningsAsErrors>false</TreatWarningsAsErrors>
				<MSBuildTreatWarningsAsErrors>false</MSBuildTreatWarningsAsErrors>
				<EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
			</PropertyGroup>

			<ItemGroup Condition="'$(CodacyParity)' == 'true' And '$(ManagePackageVersionsCentrally)' == 'true'">
				<PackageReference Include="SonarAnalyzer.CSharp" VersionOverride="10.37.0.15768" PrivateAssets="all" />
				<PackageReference Include="Meziantou.Analyzer" VersionOverride="3.0.342" PrivateAssets="all" />
			</ItemGroup>

			<ItemGroup Condition="'$(CodacyParity)' == 'true' And '$(ManagePackageVersionsCentrally)' != 'true'">
				<PackageReference Include="SonarAnalyzer.CSharp" Version="10.37.0.15768" PrivateAssets="all" />
				<PackageReference Include="Meziantou.Analyzer" Version="3.0.342" PrivateAssets="all" />
			</ItemGroup>

			<ItemGroup Condition="'$(CodacyParity)' == 'true'">
				<AdditionalFiles Include="$(MSBuildThisFileDirectory)build/CodeMetricsConfig.txt" />
			</ItemGroup>

			<!-- The SDK builds its analyzer config list before this file is evaluated, so add ours just before compiling. -->
			<Target Name="AddCodacyParityConfig" BeforeTargets="CoreCompile" Condition="'$(CodacyParity)' == 'true'">
				<ItemGroup>
					<EditorConfigFiles Include="$(MSBuildThisFileDirectory)build/codacy-parity.globalconfig" />
				</ItemGroup>
			</Target>

		</Project>
		""";

	/// <summary>
	/// The global analyzer configuration: every analyzer rule off, then the Codacy-enabled Sonar rules, CA1502 and
	/// MA0051 on as warnings.
	/// </summary>
	public static string GlobalConfigContent
	{
		get
		{
			var lines = new List<string>
			{
				"# Codacy parity (opt-in): the SonarC# rules of the organisation's Codacy coding standard, plus Lizard-like limits.",
				"# Generated by PanoramicData.NugetManagement. Used only by 'dotnet build -p:CodacyParity=true'.",
				"is_global = true",
				"global_level = 1000",
				string.Empty,
				"# Everything is off unless listed below, so only the rules Codacy runs are reported.",
				"dotnet_analyzer_diagnostic.severity = none",
				string.Empty
			};
			lines.AddRange(_sonarRuleIds.Select(id => $"dotnet_diagnostic.{id}.severity = warning"));
			lines.AddRange(
			[
				string.Empty,
				"# Lizard: a method over 8 cyclomatic complexity or 50 lines",
				"dotnet_diagnostic.CA1502.severity = warning",
				"dotnet_diagnostic.MA0051.severity = warning",
				"MA0051.maximum_lines_per_method = 50"
			]);
			return string.Join('\n', lines) + "\n";
		}
	}
}