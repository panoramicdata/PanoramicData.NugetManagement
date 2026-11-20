using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PanoramicData.NugetManagement.Web.Models;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests which licence a repository is held to: repository, then organisation, then application default.
/// </summary>
public class LicenseOverrideTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"licence-overrides-{Guid.NewGuid():n}");

	private string SettingsPath => Path.Combine(_directory, "runtime-settings.json");

	private RuntimeSettingsService NewService(string configuredLicense = "MIT")
	{
		Directory.CreateDirectory(_directory);

		return new RuntimeSettingsService(
			Options.Create(new AppSettings { NuGetOrganization = "panoramicdata", ExpectedLicense = configuredLicense }),
			NullLogger<RuntimeSettingsService>.Instance,
			SettingsPath);
	}

	[Fact]
	public void WithNoOverrides_TheConfiguredLicenceApplies()
	{
		var effective = NewService().ResolveLicense("panoramicdata/Jira.Api");

		effective.License.Should().Be("MIT");
		effective.Source.Should().Be(LicenseSource.Configuration);
	}

	[Fact]
	public void TheApplicationDefault_OverridesConfiguration()
	{
		var service = NewService();
		service.SetDefaultLicense(new LicenseOverride { License = "Apache-2.0" });

		service.ResolveLicense("panoramicdata/Jira.Api")
			.Should().Be(new EffectiveLicense("Apache-2.0", null, LicenseSource.Application));
	}

	[Fact]
	public void AnOrganisationOverride_OverridesTheDefault()
	{
		var service = NewService();
		service.SetDefaultLicense(new LicenseOverride { License = "Apache-2.0" });
		service.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "MPL-2.0" });

		service.ResolveLicense("PanoramicData/Jira.Api")
			.Should().Be(new EffectiveLicense("MPL-2.0", null, LicenseSource.Organization));
		service.ResolveLicense("someoneelse/Other.Api").License.Should().Be("Apache-2.0");
	}

	[Fact]
	public void ARepositoryOverride_OverridesTheOrganisation()
	{
		var service = NewService();
		service.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "MPL-2.0" });
		service.SetRepositoryLicense("panoramicdata/PanoramicData.EPPlus", new LicenseOverride { License = "LGPL-2.1-or-later" });

		service.ResolveLicense("panoramicdata/panoramicdata.epplus")
			.Should().Be(new EffectiveLicense("LGPL-2.1-or-later", null, LicenseSource.Repository));
		service.ResolveLicense("panoramicdata/Jira.Api").License.Should().Be("MPL-2.0");
	}

	[Fact]
	public void AnOrganisationsMarker_DoesNotFollowItsLicenceOntoARepositoryWithItsOwn()
	{
		var service = NewService();
		service.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "LGPL-2.1-or-later", LicenseFileText = "GNU LESSER" });
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "MIT" });

		service.ResolveLicense("panoramicdata/Jira.Api")
			.Should().Be(new EffectiveLicense("MIT", null, LicenseSource.Repository));
	}

	[Fact]
	public void ARepositoryMarkerAlone_KeepsTheInheritedLicence()
	{
		var service = NewService();
		service.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "LGPL-2.1-or-later" });
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { LicenseFileText = "Lesser General" });

		service.ResolveLicense("panoramicdata/Jira.Api")
			.Should().Be(new EffectiveLicense("LGPL-2.1-or-later", "Lesser General", LicenseSource.Organization));
	}

	[Fact]
	public void WhatALevelInherits_IgnoresItselfAndEverythingMoreSpecific()
	{
		var service = NewService();
		service.SetDefaultLicense(new LicenseOverride { License = "Apache-2.0" });
		service.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "MPL-2.0" });
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "MIT AND BSD-2-Clause" });

		service.ResolveInherited("panoramicdata/Jira.Api", LicenseSource.Repository)
			.Should().Be(new EffectiveLicense("MPL-2.0", null, LicenseSource.Organization));
		service.ResolveInherited("panoramicdata/", LicenseSource.Organization)
			.Should().Be(new EffectiveLicense("Apache-2.0", null, LicenseSource.Application));
		service.ResolveInherited(null, LicenseSource.Application)
			.Should().Be(new EffectiveLicense("MIT", null, LicenseSource.Configuration));
	}

	[Fact]
	public void BlankOverrides_InheritAndAreRemoved()
	{
		var service = NewService();
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "MIT AND BSD-2-Clause" });
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "  ", LicenseFileText = "" });

		service.GetRepositoryLicense("panoramicdata/Jira.Api").Should().BeNull();
		service.ResolveLicense("panoramicdata/Jira.Api").Source.Should().Be(LicenseSource.Configuration);
	}

	[Fact]
	public void Overrides_SurviveAReload_AndAreLookedUpWhateverTheCase()
	{
		var first = NewService();
		first.SetDefaultLicense(new LicenseOverride { License = "Apache-2.0" });
		first.SetOrganizationLicense("PanoramicData", new LicenseOverride { License = "MPL-2.0", LicenseFileText = "Mozilla" });
		first.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "MIT AND BSD-2-Clause" });

		var second = NewService();

		second.DefaultLicense!.License.Should().Be("Apache-2.0");
		second.GetOrganizationLicense("panoramicdata")!.LicenseFileText.Should().Be("Mozilla");
		second.ResolveLicense("PANORAMICDATA/JIRA.API")
			.Should().Be(new EffectiveLicense("MIT AND BSD-2-Clause", null, LicenseSource.Repository));
	}

	[Fact]
	public void Overrides_SurviveAnUnrelatedSave()
	{
		var first = NewService();
		first.SetRepositoryLicense("panoramicdata/PanoramicData.EPPlus", new LicenseOverride { License = "LGPL-2.1-or-later" });
		first.SetOrganizationLicense("panoramicdata", new LicenseOverride { License = "MPL-2.0" });
		first.SetDefaultLicense(new LicenseOverride { License = "Apache-2.0" });
		first.SetPreferredIdeId("vscode");

		var second = NewService();

		second.ResolveLicense("panoramicdata/PanoramicData.EPPlus").License.Should().Be("LGPL-2.1-or-later");
		second.GetOrganizationLicense("panoramicdata")!.License.Should().Be("MPL-2.0");
		second.DefaultLicense!.License.Should().Be("Apache-2.0");
	}

	[Fact]
	public void ASettingsFileFromBeforeOverrides_LoadsWithNone()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllText(SettingsPath, """{ "PreferredIdeId": "vscode", "OrganizationLicenses": null }""");

		var service = NewService();

		service.ResolveLicense("panoramicdata/Jira.Api").Should().Be(new EffectiveLicense("MIT", null, LicenseSource.Configuration));
		service.SetRepositoryLicense("panoramicdata/Jira.Api", new LicenseOverride { License = "MIT AND BSD-2-Clause" });
		service.ResolveLicense("panoramicdata/Jira.Api").License.Should().Be("MIT AND BSD-2-Clause");
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (Directory.Exists(_directory))
		{
			Directory.Delete(_directory, recursive: true);
		}

		GC.SuppressFinalize(this);
	}
}
