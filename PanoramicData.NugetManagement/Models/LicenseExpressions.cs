using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// What an SPDX licence expression means for the files the licensing rules check.
/// </summary>
public static partial class LicenseExpressions
{
	private static readonly Dictionary<string, string> _knownMarkers = new(StringComparer.OrdinalIgnoreCase)
	{
		["MIT"] = "MIT License",
		["Apache-2.0"] = "Apache License",
		["BSD-2-Clause"] = "BSD 2-Clause License",
		["BSD-3-Clause"] = "BSD 3-Clause License",
		["MPL-2.0"] = "Mozilla Public License",
		["ISC"] = "ISC License",
		["Unlicense"] = "This is free and unencumbered software released into the public domain",
	};

	// The labels the widely copied shields.io licence badges use, so an existing badge is recognised.
	private static readonly Dictionary<string, string> _conventionalBadgeLabels = new(StringComparer.OrdinalIgnoreCase)
	{
		["Apache-2.0"] = "Apache 2.0",
		["LGPL-2.1-only"] = "LGPL v2.1",
		["LGPL-2.1-or-later"] = "LGPL v2.1 or later",
		["LGPL-3.0-only"] = "LGPL v3",
		["LGPL-3.0-or-later"] = "LGPL v3 or later",
		["GPL-2.0-only"] = "GPL v2",
		["GPL-2.0-or-later"] = "GPL v2 or later",
		["GPL-3.0-only"] = "GPL v3",
		["GPL-3.0-or-later"] = "GPL v3 or later",
		["AGPL-3.0-only"] = "AGPL v3",
		["AGPL-3.0-or-later"] = "AGPL v3 or later",
		["MPL-2.0"] = "MPL 2.0",
		["BSD-2-Clause"] = "BSD 2-Clause",
		["BSD-3-Clause"] = "BSD 3-Clause",
	};

	/// <summary>
	/// Whether the expression is the estate default, <see cref="Standards.LicenseType"/>.
	/// </summary>
	/// <param name="expression">The SPDX expression.</param>
	public static bool IsDefault(string? expression)
		=> string.Equals(expression?.Trim(), Standards.LicenseType, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The licence identifiers in an expression, without operators, exceptions or parentheses.
	/// </summary>
	/// <param name="expression">The SPDX expression, e.g. "MIT AND BSD-2-Clause".</param>
	public static IReadOnlyList<string> Identifiers(string expression)
	{
		var withoutExceptions = WithExceptionRegex().Replace(expression ?? string.Empty, string.Empty);
		return
		[
			.. OperatorRegex()
				.Split(withoutExceptions.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal))
				.Select(part => part.Trim())
				.Where(part => part.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		];
	}

	/// <summary>
	/// The texts a LICENSE file must contain for the expression: one per licence it names.
	/// </summary>
	/// <param name="expression">The SPDX expression.</param>
	public static IReadOnlyList<string> FileMarkers(string expression)
		=> [.. Identifiers(expression).Select(FileMarker).Distinct(StringComparer.OrdinalIgnoreCase)];

	private static string FileMarker(string identifier)
	{
		if (_knownMarkers.TryGetValue(identifier, out var marker))
		{
			return marker;
		}

		// The licence texts title themselves in capitals and never name the SPDX id.
		if (identifier.StartsWith("LGPL-", StringComparison.OrdinalIgnoreCase))
		{
			return "GNU LESSER GENERAL PUBLIC LICENSE";
		}

		if (identifier.StartsWith("AGPL-", StringComparison.OrdinalIgnoreCase))
		{
			return "GNU AFFERO GENERAL PUBLIC LICENSE";
		}

		return identifier.StartsWith("GPL-", StringComparison.OrdinalIgnoreCase)
			? "GNU GENERAL PUBLIC LICENSE"
			: $"{identifier} License";
	}

	/// <summary>
	/// The expression escaped for a shields.io static badge path segment.
	/// </summary>
	/// <param name="text">The badge text.</param>
	public static string ShieldsSegment(string text)
		=> Uri.EscapeDataString(text
			.Replace("-", "--", StringComparison.Ordinal)
			.Replace("_", "__", StringComparison.Ordinal)
			.Replace(" ", "_", StringComparison.Ordinal));

	/// <summary>
	/// The README texts that show a badge for the expression: its alt text or its shields.io segment.
	/// </summary>
	/// <param name="expression">The SPDX expression.</param>
	public static IReadOnlyList<string> BadgeMarkers(string expression)
	{
		var labels = new List<string> { expression };
		if (_conventionalBadgeLabels.TryGetValue(expression, out var conventional))
		{
			labels.Add(conventional);
		}

		return
		[
			.. labels.SelectMany(label => new[] { $"License: {label}", $"License-{ShieldsSegment(label)}", $"license-{label}" })
				.Distinct(StringComparer.OrdinalIgnoreCase)
		];
	}

	/// <summary>
	/// The Markdown badge line README-04 adds for the expression.
	/// </summary>
	/// <param name="expression">The SPDX expression.</param>
	public static string BadgeLine(string expression)
	{
		if (IsDefault(expression))
		{
			return $"[![License: {expression}](https://img.shields.io/badge/License-{expression}-yellow.svg)](https://opensource.org/licenses/{expression})";
		}

		var link = Identifiers(expression).Count == 1
			? $"https://spdx.org/licenses/{Uri.EscapeDataString(expression.Trim())}.html"
			: "LICENSE";
		return $"[![License: {expression}](https://img.shields.io/badge/License-{ShieldsSegment(expression)}-blue.svg)]({link})";
	}

	[GeneratedRegex(@"\s+WITH\s+\S+", RegexOptions.IgnoreCase)]
	private static partial Regex WithExceptionRegex();

	[GeneratedRegex(@"\s+(?:AND|OR)\s+", RegexOptions.IgnoreCase)]
	private static partial Regex OperatorRegex();
}
