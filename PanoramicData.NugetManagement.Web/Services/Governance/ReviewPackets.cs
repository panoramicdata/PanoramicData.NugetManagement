using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.Governance;

/// <summary>
/// Removes anything that looks like a credential from text before it is kept.
/// </summary>
/// <remarks>
/// Written after a test run printed a live API key into a transcript. A review packet is stored for a
/// year and read by people and models, so it is the last place a secret should be able to survive: what
/// can be recognised is replaced, and a packet that still looks like it holds one afterwards is refused
/// outright by <see cref="ReviewPacketStore"/> and not stored with a warning.
/// <para>
/// Pattern-based, so it catches the shapes it knows — keys and tokens in assignments, bearer headers,
/// well-known token formats, private-key blocks — and nothing else. It is a net, not a proof.
/// </para>
/// </remarks>
public static partial class PacketScrubber
{
	/// <summary>The text that stands in for a removed secret.</summary>
	public const string Redaction = "[redacted]";

	/// <summary>
	/// The text with every recognisable credential replaced.
	/// </summary>
	/// <param name="text">The text to scrub.</param>
	public static string Scrub(string text)
	{
		var result = PrivateKeyBlock().Replace(text, Redaction);
		result = Assignment().Replace(result, match => $"{match.Groups["name"].Value}{match.Groups["sep"].Value}{Redaction}");
		result = Bearer().Replace(result, $"Bearer {Redaction}");
		result = KnownTokens().Replace(result, Redaction);

		return result;
	}

	/// <summary>
	/// Whether the text still looks like it contains a credential.
	/// </summary>
	/// <param name="text">The text to check, normally after <see cref="Scrub"/>.</param>
	public static bool LooksLikeItHoldsASecret(string text)
		=> PrivateKeyBlock().IsMatch(text)
			|| KnownTokens().IsMatch(text)
			|| Bearer().Matches(text).Any(match => !match.Value.EndsWith(Redaction, StringComparison.Ordinal))
			|| Assignment().Matches(text).Any(match => !match.Groups["value"].Value.StartsWith(Redaction, StringComparison.Ordinal));

	[GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
	private static partial Regex PrivateKeyBlock();

	// name=value or name: value, where the name says it is a secret and the value is more than a few characters.
	[GeneratedRegex(
		@"(?<name>(?i:api[-_ ]?key|apikey|access[-_ ]?key|secret[-_ ]?key|client[-_ ]?secret|token|secret|password|passwd|pwd|authorization))(?<sep>[""']?\s*[=:]\s*[""']?)(?<value>[^\s""'&,;]{8,})")]
	private static partial Regex Assignment();

	[GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{16,}")]
	private static partial Regex Bearer();

	// GitHub tokens, AWS access key ids, Slack tokens, and JWT-shaped strings.
	[GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{30,}\b|\bAKIA[0-9A-Z]{16}\b|\bxox[abprs]-[A-Za-z0-9-]{10,}\b|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b")]
	private static partial Regex KnownTokens();
}

/// <summary>Where a stored packet is, and how to tell it has not changed.</summary>
/// <param name="RelativePath">The packet file, relative to the packet store's folder.</param>
/// <param name="Sha256">The hash of the packet as stored (compressed).</param>
/// <param name="Bytes">Its size on disk.</param>
public sealed record PacketReference(string RelativePath, string Sha256, long Bytes);

/// <summary>Raised when a packet cannot be made safe to keep.</summary>
/// <param name="message">What was wrong.</param>
public sealed class PacketRefusedException(string message) : Exception(message);

/// <summary>
/// Stores review packets: the evidence a later reviewer needs to judge an action.
/// </summary>
/// <remarks>
/// Session logs expire after thirty days and the ledger is kept for a year, so the ledger entry for an AI
/// session points at a packet that holds what the log would have: the prompt, the calls in order, the
/// checks, the diff, what happened afterwards. Compressed JSON, one file each, filed by month beside the
/// ledger so retention can remove them together.
/// </remarks>
/// <param name="folder">The packet folder.</param>
/// <param name="clock">The clock; the system's when null.</param>
public sealed class ReviewPacketStore(string folder, TimeProvider? clock = null)
{
	private static readonly JsonSerializerOptions _json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private readonly TimeProvider _clock = clock ?? TimeProvider.System;

	/// <summary>The default location, beside the ledger.</summary>
	public static string DefaultFolder { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"ledger",
		"packets");

	/// <summary>
	/// Writes a packet, scrubbed, and says where it went.
	/// </summary>
	/// <param name="packet">Anything the JSON serialiser can write.</param>
	/// <exception cref="PacketRefusedException">When the packet still looks like it holds a credential once scrubbed.</exception>
	public PacketReference Write(object packet)
	{
		var scrubbed = PacketScrubber.Scrub(JsonSerializer.Serialize(packet, _json));

		if (PacketScrubber.LooksLikeItHoldsASecret(scrubbed))
		{
			throw new PacketRefusedException("The packet still looks like it contains a credential after scrubbing, so it was not stored.");
		}

		var month = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
		var relative = Path.Combine(month, $"{Guid.NewGuid():n}.json.gz");
		var full = Path.Combine(folder, relative);

		Directory.CreateDirectory(Path.GetDirectoryName(full)!);

		using (var file = File.Create(full))
		using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
		{
			gzip.Write(Encoding.UTF8.GetBytes(scrubbed));
		}

		var bytes = File.ReadAllBytes(full);

		return new PacketReference(relative.Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
	}

	/// <summary>
	/// Reads a packet back, checking it still matches the hash recorded when it was written.
	/// </summary>
	/// <param name="reference">The reference the ledger holds.</param>
	/// <returns>The packet as JSON, or null when the file is gone.</returns>
	/// <exception cref="InvalidDataException">When the file's hash no longer matches the reference.</exception>
	public JsonElement? Read(PacketReference reference)
	{
		var full = Path.GetFullPath(Path.Combine(folder, reference.RelativePath));

		// The reference comes from the ledger, but a path is a path: never read outside the store.
		if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The packet reference points outside the packet store.");
		}

		if (!File.Exists(full))
		{
			return null;
		}

		var bytes = File.ReadAllBytes(full);

		if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The packet has changed since it was written.");
		}

		using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
		using var document = JsonDocument.Parse(gzip);

		return document.RootElement.Clone();
	}

	/// <summary>
	/// Removes packet folders for months older than the retention period.
	/// </summary>
	/// <param name="months">How many months to keep, counting the current one.</param>
	/// <returns>The month folders removed.</returns>
	public IReadOnlyList<string> ApplyRetention(int months)
	{
		if (months < 1 || !Directory.Exists(folder))
		{
			return [];
		}

		var oldest = _clock.GetUtcNow().UtcDateTime.AddMonths(-(months - 1)).ToString("yyyy-MM", CultureInfo.InvariantCulture);
		var removed = new List<string>();

		foreach (var directory in Directory.EnumerateDirectories(folder))
		{
			var name = Path.GetFileName(directory);

			if (string.CompareOrdinal(name, oldest) < 0)
			{
				Directory.Delete(directory, recursive: true);
				removed.Add(name);
			}
		}

		return removed;
	}
}
