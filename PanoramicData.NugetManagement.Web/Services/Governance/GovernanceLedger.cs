using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services.Governance;

/// <summary>
/// One record of something the system did, or something a person or reviewer decided about it.
/// </summary>
/// <param name="Seq">Position in the ledger, starting at 1 and never skipping.</param>
/// <param name="AtUtc">When it was written.</param>
/// <param name="Actor">Who: <c>autopilot</c>, <c>user</c>, <c>agent-api</c>, <c>claude-review</c> or <c>claude-session</c>.</param>
/// <param name="Kind">What sort of entry; see the design for the list.</param>
/// <param name="Repository">The repository it concerns, as "owner/name", or null.</param>
/// <param name="RuleId">The rule it concerns, or null.</param>
/// <param name="PolicyHash">The hash of the policy in force, so an action can be tied to the rules it was taken under.</param>
/// <param name="Data">The entry's own fields, whatever its kind needs.</param>
/// <param name="PrevHash">The hash of the entry before this one.</param>
/// <param name="Hash">This entry's hash.</param>
public sealed record LedgerEntry(
	long Seq,
	DateTimeOffset AtUtc,
	string Actor,
	string Kind,
	string? Repository,
	string? RuleId,
	string? PolicyHash,
	JsonElement Data,
	string PrevHash,
	string Hash);

/// <summary>
/// The result of walking the chain.
/// </summary>
/// <param name="Intact">Whether every entry links to the one before and hashes to what it says.</param>
/// <param name="Entries">How many entries were checked.</param>
/// <param name="FirstBrokenSeq">The first entry that failed, or null when the chain is intact.</param>
/// <param name="Problem">What was wrong with it.</param>
/// <param name="HeadHash">The hash of the last entry checked.</param>
/// <param name="StartsAfterRetention">True when the oldest remaining entry is not the first ever written, because older months were removed.</param>
public sealed record LedgerVerification(
	bool Intact,
	long Entries,
	long? FirstBrokenSeq,
	string? Problem,
	string? HeadHash,
	bool StartsAfterRetention);

/// <summary>
/// The append-only record of what Autopilot, the people around it and the reviewer did.
/// </summary>
/// <remarks>
/// Each entry carries the SHA-256 of the one before it, so editing, deleting or reordering any entry breaks
/// the chain from that point on, and <see cref="Verify"/> says where. It proves the record is whole; it does
/// not prove the record is true, which is what the review packets and the reviewer are for.
/// <para>
/// A line is hashed as written. The body is built once, the hash taken over exactly those bytes, and then
/// appended, so verification re-reads the text and never has to reproduce how a serialiser would have
/// written it — the usual way a hash chain ends up failing against itself.
/// </para>
/// <para>
/// One file per month, <c>ledger-yyyy-MM.jsonl</c>. Retention removes whole months only.
/// </para>
/// </remarks>
public sealed partial class GovernanceLedger
{
	/// <summary>The hash a chain starts from.</summary>
	public static readonly string Genesis = new('0', 64);

	/// <summary>The default location, under the application's data folder.</summary>
	public static string DefaultFolder { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		"ledger");

	private readonly Lock _gate = new();
	private readonly string _directory;
	private readonly Func<string?> _policyHash;
	private readonly TimeProvider _clock;

	private long _seq;
	private string _head = Genesis;

	/// <summary>
	/// Opens the ledger in a folder, continuing from whatever is already there.
	/// </summary>
	/// <param name="directory">The folder the monthly files live in.</param>
	/// <param name="policyHash">Supplies the hash of the policy in force, stamped on every entry.</param>
	/// <param name="clock">The clock; the system's when null.</param>
	public GovernanceLedger(string directory, Func<string?>? policyHash = null, TimeProvider? clock = null)
	{
		_directory = directory;
		_policyHash = policyHash ?? (() => null);
		_clock = clock ?? TimeProvider.System;

		Directory.CreateDirectory(directory);

		// Continue from the last entry on disk, so a restart extends the chain rather than starting a second.
		var last = ReadLines().LastOrDefault();

		if (last is not null)
		{
			var entry = Parse(last);
			_seq = entry.Seq;
			_head = entry.Hash;
		}
	}

	/// <summary>The folder the ledger is kept in.</summary>
	public string Folder => _directory;

	/// <summary>The hash of the newest entry.</summary>
	public string Head
	{
		get
		{
			lock (_gate)
			{
				return _head;
			}
		}
	}

	/// <summary>
	/// Writes one entry and returns it.
	/// </summary>
	/// <param name="actor">Who is responsible.</param>
	/// <param name="kind">What sort of entry it is.</param>
	/// <param name="repository">The repository it concerns, if any.</param>
	/// <param name="ruleId">The rule it concerns, if any.</param>
	/// <param name="data">The entry's own fields; anything the JSON serialiser can write.</param>
	public LedgerEntry Append(string actor, string kind, string? repository = null, string? ruleId = null, object? data = null)
	{
		var dataElement = JsonSerializer.SerializeToElement(data ?? new { }, _json);

		lock (_gate)
		{
			var now = _clock.GetUtcNow();
			var seq = _seq + 1;
			var policyHash = _policyHash();

			var previous = _head;
			var body = Body(seq, now, actor, kind, repository, ruleId, policyHash, dataElement, previous);
			var hash = Sha256(body);
			var line = string.Concat(body.AsSpan(0, body.Length - 1), ",\"hash\":\"", hash, "\"}");

			var path = Path.Combine(_directory, $"ledger-{now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl");
			File.AppendAllText(path, line + "\n", new UTF8Encoding(false));

			_seq = seq;
			_head = hash;

			return new LedgerEntry(seq, now, actor, kind, repository, ruleId, policyHash, dataElement, previous, hash);
		}
	}

	/// <summary>
	/// Entries, newest first.
	/// </summary>
	/// <param name="kind">Only this kind, or null for all.</param>
	/// <param name="repository">Only this repository, or null for all.</param>
	/// <param name="take">At most this many.</param>
	/// <param name="beforeSeq">Only entries older than this one, for paging.</param>
	public IReadOnlyList<LedgerEntry> Query(string? kind = null, string? repository = null, int take = 100, long? beforeSeq = null)
	{
		lock (_gate)
		{
			return [.. ReadLines()
				.Select(Parse)
				.Where(entry => kind is null || string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
				.Where(entry => repository is null || string.Equals(entry.Repository, repository, StringComparison.OrdinalIgnoreCase))
				.Where(entry => beforeSeq is null || entry.Seq < beforeSeq)
				.Reverse()
				.Take(Math.Clamp(take, 1, 1000))];
		}
	}

	/// <summary>
	/// Walks the whole chain and reports where it first fails.
	/// </summary>
	public LedgerVerification Verify()
	{
		lock (_gate)
		{
			var previousHash = (string?)null;
			long previousSeq = 0;
			long count = 0;
			var startsAfterRetention = false;

			foreach (var line in ReadLines())
			{
				var match = HashSuffix().Match(line);

				if (!match.Success)
				{
					return new LedgerVerification(false, count, previousSeq + 1, "an entry has no hash", previousHash, startsAfterRetention);
				}

				var body = string.Concat(line.AsSpan(0, match.Index), "}");
				var claimed = match.Groups["hash"].Value;
				LedgerEntry entry;

				try
				{
					entry = Parse(line);
				}
				catch (JsonException)
				{
					return new LedgerVerification(false, count, previousSeq + 1, "an entry is not valid JSON", previousHash, startsAfterRetention);
				}

				if (!string.Equals(Sha256(body), claimed, StringComparison.Ordinal))
				{
					return new LedgerVerification(false, count, entry.Seq, "an entry's text no longer matches its hash", previousHash, startsAfterRetention);
				}

				if (previousHash is null)
				{
					// The first entry we can see. If it is not the first ever written, older months were
					// removed by retention and its predecessor is no longer here to check against.
					startsAfterRetention = entry.Seq != 1;
				}
				else
				{
					if (entry.Seq != previousSeq + 1)
					{
						return new LedgerVerification(false, count, entry.Seq, $"expected entry {previousSeq + 1} but found {entry.Seq}: an entry is missing or out of order", previousHash, startsAfterRetention);
					}

					if (!string.Equals(entry.PrevHash, previousHash, StringComparison.Ordinal))
					{
						return new LedgerVerification(false, count, entry.Seq, "an entry does not link to the one before it", previousHash, startsAfterRetention);
					}
				}

				previousHash = claimed;
				previousSeq = entry.Seq;
				count++;
			}

			return new LedgerVerification(true, count, null, null, previousHash, startsAfterRetention);
		}
	}

	/// <summary>
	/// Removes months older than the retention period, whole files only, and records that it did.
	/// </summary>
	/// <param name="months">How many months to keep, counting the current one.</param>
	/// <returns>The names of the files removed.</returns>
	public IReadOnlyList<string> ApplyRetention(int months)
	{
		if (months < 1)
		{
			return [];
		}

		var cutoff = _clock.GetUtcNow().UtcDateTime.AddMonths(-(months - 1));
		var oldest = $"ledger-{cutoff.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl";

		var removed = new List<string>();
		string? lastHash = null;

		lock (_gate)
		{
			foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "ledger-*.jsonl").Order(StringComparer.Ordinal))
			{
				var name = Path.GetFileName(file);

				if (string.CompareOrdinal(name, oldest) >= 0)
				{
					continue;
				}

				lastHash = File.ReadLines(file).LastOrDefault() is { } last ? Parse(last).Hash : lastHash;
				File.Delete(file);
				removed.Add(name);
			}
		}

		if (removed.Count > 0)
		{
			Append("autopilot", "retention_applied", data: new { months, removed, lastHashRemoved = lastHash });
		}

		return removed;
	}

	private IEnumerable<string> ReadLines()
		=> System.IO.Directory.EnumerateFiles(_directory, "ledger-*.jsonl")
			.Order(StringComparer.Ordinal)
			.SelectMany(File.ReadLines)
			.Where(line => line.Length > 0);

	private static LedgerEntry Parse(string line)
	{
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;

		string? Optional(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

		return new LedgerEntry(
			root.GetProperty("seq").GetInt64(),
			DateTimeOffset.Parse(root.GetProperty("atUtc").GetString()!, CultureInfo.InvariantCulture),
			root.GetProperty("actor").GetString()!,
			root.GetProperty("kind").GetString()!,
			Optional("repository"),
			Optional("ruleId"),
			Optional("policyHash"),
			root.GetProperty("data").Clone(),
			root.GetProperty("prevHash").GetString()!,
			root.GetProperty("hash").GetString()!);
	}

	private static string Body(
		long seq,
		DateTimeOffset at,
		string actor,
		string kind,
		string? repository,
		string? ruleId,
		string? policyHash,
		JsonElement data,
		string prevHash)
	{
		using var stream = new MemoryStream();

		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			writer.WriteNumber("seq", seq);
			writer.WriteString("atUtc", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
			writer.WriteString("actor", actor);
			writer.WriteString("kind", kind);

			if (repository is not null)
			{
				writer.WriteString("repository", repository);
			}

			if (ruleId is not null)
			{
				writer.WriteString("ruleId", ruleId);
			}

			if (policyHash is not null)
			{
				writer.WriteString("policyHash", policyHash);
			}

			writer.WritePropertyName("data");
			data.WriteTo(writer);
			writer.WriteString("prevHash", prevHash);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string Sha256(string text)
		=> Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	[GeneratedRegex(@",""hash"":""(?<hash>[0-9a-f]{64})""\}$")]
	private static partial Regex HashSuffix();
}
