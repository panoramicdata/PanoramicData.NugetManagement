using System.Text.Json;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for the serialisable description of a unit of work. The catalogue is closed so that
/// queued work can be written to disk and picked up again after a restart; these tests are what
/// stop a kind being added that cannot survive that round trip.
/// </summary>
public class WorkDescriptorTests(ITestOutputHelper output) : TestWithOutput(output)
{
	[Fact]
	public void LaneKey_RepositoryScoped_IsTheRepositoryLane()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", "panoramicdata/Athonet.Api")
			.LaneKey.Should().Be("repo:panoramicdata/athonet.api");

	[Fact]
	public void LaneKey_OrganizationScoped_IsTheOrganizationLane()
		=> WorkDescriptor.ForOrganization(WorkKind.RediscoverOrganization, "panoramicdata")
			.LaneKey.Should().Be("org:panoramicdata");

	[Fact]
	public void LaneKey_RepositoryCasing_IsNormalised()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "panoramicdata", "PanoramicData/Athonet.Api")
			.LaneKey.Should().Be("repo:panoramicdata/athonet.api");

	[Fact]
	public void Parameter_Absent_IsNull()
		=> WorkDescriptor.ForRepository(WorkKind.Build, "org", "org/repo")
			.Parameter("ruleId").Should().BeNull();

	[Fact]
	public void Parameter_Present_IsTheValue()
		=> WorkDescriptor.ForRepository(WorkKind.FixRule, "org", "org/repo", ("ruleId", "TST-06"))
			.Parameter("ruleId").Should().Be("TST-06");

	[Theory]
	[MemberData(nameof(AllKinds))]
	public void RoundTrip_EveryKind_SurvivesJson(WorkKind kind)
	{
		var original = new WorkDescriptor(kind, "panoramicdata", "panoramicdata/Athonet.Api",
			new Dictionary<string, string> { ["ruleId"] = "TST-06", ["category"] = "NuGetHygiene" });

		var restored = JsonSerializer.Deserialize<WorkDescriptor>(JsonSerializer.Serialize(original));

		restored.Should().NotBeNull();
		restored!.Kind.Should().Be(kind);
		restored.Organization.Should().Be("panoramicdata");
		restored.RepositoryFullName.Should().Be("panoramicdata/Athonet.Api");
		restored.Parameter("ruleId").Should().Be("TST-06");
	}

	public static TheoryData<WorkKind> AllKinds()
	{
		var data = new TheoryData<WorkKind>();
		foreach (var kind in Enum.GetValues<WorkKind>())
		{
			data.Add(kind);
		}

		return data;
	}
}
