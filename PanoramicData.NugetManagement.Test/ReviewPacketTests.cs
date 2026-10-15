using PanoramicData.NugetManagement.Web.Services.Governance;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for review packets and the scrubber that keeps credentials out of them. The scrubber exists because
/// a test run once printed a live API key into a transcript.
/// </summary>
public class ReviewPacketTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _folder = Path.Combine(
		Path.GetTempPath(),
		"nugetmanagement-tests",
		$"packets-{Guid.NewGuid():n}");

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Theory]
	[InlineData("DMAPI Request: GET /request/query-whois?domain=joker.com&api-key=69ae5732fe30d615851e4549e6b7151c56fe51f835c618d175a5eed2f71609d4", "69ae5732")]
	[InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnop")]
	[InlineData("password = hunter2hunter2", "hunter2hunter2")]
	[InlineData("\"token\": \"ghp_abcdefghijklmnopqrstuvwxyz0123456789\"", "ghp_abcdef")]
	[InlineData("key AKIAABCDEFGHIJKLMNOP here", "AKIAABCDEFGHIJKLMNOP")]
	[InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r", "eyJhbGciOiJIUzI1NiJ9")]
	[InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----", "MIIEowIBAAKCAQEA")]
	public void Scrub_RemovesWhatLooksLikeACredential(string text, string secret)
	{
		var scrubbed = PacketScrubber.Scrub(text);

		scrubbed.Should().NotContain(secret);
		scrubbed.Should().Contain(PacketScrubber.Redaction);
		PacketScrubber.LooksLikeItHoldsASecret(scrubbed).Should().BeFalse();
	}

	[Fact]
	public void Scrub_KeepsTheRestOfTheLine_SoTheEvidenceStillReads()
		=> PacketScrubber.Scrub("GET /query?domain=joker.com&api-key=69ae5732fe30d615851e4549e6b7151c")
			.Should().StartWith("GET /query?domain=joker.com&api-key=");

	[Theory]
	[InlineData("The build failed with 3 errors in Program.cs")]
	[InlineData("tokens used: 4,312 in 5.2s")]
	[InlineData("PATH=C:\\Windows;C:\\Windows\\System32")]
	[InlineData("replace_in_file src/KeyHandler.cs: Replaced the text")]
	[InlineData("password policy documented in SECURITY.md")]
	public void Scrub_LeavesOrdinaryTextAlone(string text)
		=> PacketScrubber.Scrub(text).Should().Be(text);

	[Fact]
	public void Write_StoresAScrubbedCompressedPacket_ThatReadsBack()
	{
		var store = new ReviewPacketStore(_folder);

		var reference = store.Write(new
		{
			task = "Fix TST-10",
			calls = new[] { new { name = "write_file", path = "a.cs" } },
			note = "request had api-key=69ae5732fe30d615851e4549e6b7151c56fe51f8"
		});

		reference.RelativePath.Should().EndWith(".json.gz");
		reference.Bytes.Should().BeGreaterThan(0);
		File.Exists(Path.Combine(_folder, reference.RelativePath)).Should().BeTrue();

		var packet = store.Read(reference)!.Value;
		packet.GetProperty("task").GetString().Should().Be("Fix TST-10");
		packet.GetProperty("note").GetString().Should().NotContain("69ae5732").And.Contain(PacketScrubber.Redaction);
		packet.GetProperty("calls")[0].GetProperty("name").GetString().Should().Be("write_file");
	}

	[Fact]
	public void Write_OnDisk_NeverContainsTheSecret()
	{
		var store = new ReviewPacketStore(_folder);
		var reference = store.Write(new { note = "api-key=69ae5732fe30d615851e4549e6b7151c56fe51f8" });

		using var file = File.OpenRead(Path.Combine(_folder, reference.RelativePath));
		using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
		using var reader = new StreamReader(gzip);

		reader.ReadToEnd().Should().NotContain("69ae5732");
	}

	[Fact]
	public void Read_ADifferentFileUnderTheSameName_IsRefused()
	{
		var store = new ReviewPacketStore(_folder);
		var reference = store.Write(new { a = 1 });

		var other = store.Write(new { b = 2 });
		File.Copy(Path.Combine(_folder, other.RelativePath), Path.Combine(_folder, reference.RelativePath), overwrite: true);

		var act = () => store.Read(reference);

		act.Should().Throw<InvalidDataException>().WithMessage("*changed*");
	}

	[Fact]
	public void Read_AMissingPacket_IsNullRatherThanAnError()
		=> new ReviewPacketStore(_folder).Read(new PacketReference("2026-01/none.json.gz", "x", 0)).Should().BeNull();

	[Fact]
	public void Read_APathThatEscapesTheStore_IsRefused()
	{
		var act = () => new ReviewPacketStore(_folder).Read(new PacketReference("../../outside.json.gz", "x", 0));

		act.Should().Throw<InvalidDataException>().WithMessage("*outside*");
	}

	[Fact]
	public void Write_ARepeatedPacket_GetsItsOwnFile()
	{
		var store = new ReviewPacketStore(_folder);

		store.Write(new { a = 1 }).RelativePath.Should().NotBe(store.Write(new { a = 1 }).RelativePath);
	}

	[Fact]
	public void ApplyRetention_RemovesOldMonthFolders_AndKeepsTheRest()
	{
		Directory.CreateDirectory(Path.Combine(_folder, "2025-01"));
		Directory.CreateDirectory(Path.Combine(_folder, "2026-10"));
		var store = new ReviewPacketStore(_folder, new FixedClock(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)));

		store.ApplyRetention(months: 12).Should().Equal("2025-01");

		Directory.Exists(Path.Combine(_folder, "2025-01")).Should().BeFalse();
		Directory.Exists(Path.Combine(_folder, "2026-10")).Should().BeTrue();
	}

	private sealed class FixedClock(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	[Fact]
	public void LooksLikeItHoldsASecret_IsFalseForScrubbedText_AndTrueForRaw()
	{
		const string raw = "api-key=69ae5732fe30d615851e4549e6b7151c56fe51f8";

		PacketScrubber.LooksLikeItHoldsASecret(raw).Should().BeTrue();
		PacketScrubber.LooksLikeItHoldsASecret(PacketScrubber.Scrub(raw)).Should().BeFalse();
	}
}
