using System.Globalization;

namespace RoseMcp.Ui.Core.Updates;

/// <summary>
/// A released version, and the one comparison that matters: is the other one newer than this.
/// <para>
/// Semantic versioning rather than <see cref="Version"/>, because the whole question turns on the
/// part <see cref="Version"/> cannot hold. Releases here are tagged <c>v0.3.0</c> and
/// <c>v0.3.0-rc.1</c>, and a release candidate precedes the release it is a candidate for -- so a
/// comparison that ignores the suffix reports 0.3.0-rc.1 and 0.3.0 as the same version and never
/// offers the upgrade.
/// </para>
/// </summary>
public readonly record struct ReleaseVersion
{
	private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		Prerelease = prerelease;
	}

	public int Major { get; }

	public int Minor { get; }

	public int Patch { get; }

	/// <summary>
	/// The dot-separated identifiers after the hyphen, empty for a released version. Empty is the
	/// higher of the two: a version with no prerelease outranks every prerelease of the same numbers.
	/// </summary>
	public string[] Prerelease { get; }

	/// <summary>
	/// Reads a tag or a version string. Accepts the leading <c>v</c> that git tags carry here, and
	/// discards build metadata after '+', which semver says takes no part in precedence -- for these
	/// builds it is the commit hash, so comparing it would report every rebuild as an upgrade.
	/// </summary>
	public static bool TryParse(string? text, out ReleaseVersion version)
	{
		version = default;
		if (string.IsNullOrWhiteSpace(text)) return false;

		var span = text.Trim();
		if (span.StartsWith("v", StringComparison.OrdinalIgnoreCase)) span = span[1..];

		var plus = span.IndexOf('+');
		if (plus >= 0) span = span[..plus];

		var prerelease = Array.Empty<string>();
		var hyphen = span.IndexOf('-');
		if (hyphen >= 0)
		{
			prerelease = span[(hyphen + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
			span = span[..hyphen];
		}

		var parts = span.Split('.');
		if (parts.Length is < 1 or > 3) return false;

		// A tag may be 1.2 or even 1, and the missing places are zero. Refusing those would mean an
		// update check that silently stops working the first time somebody tags one.
		var numbers = new int[3];
		for (var i = 0; i < parts.Length; i++)
		{
			if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
		}

		version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], prerelease);

		return true;
	}

	/// <summary>
	/// Whether <paramref name="other"/> is a version somebody on this one would want.
	/// </summary>
	public bool IsOlderThan(ReleaseVersion other) => Compare(this, other) < 0;

	/// <summary>
	/// Semantic version precedence. Numbers first, then the prerelease rules: having none wins, and
	/// otherwise identifiers are compared one at a time, numerically when both are numeric and
	/// alphabetically when not, with a numeric identifier ranking below an alphanumeric one.
	/// </summary>
	public static int Compare(ReleaseVersion left, ReleaseVersion right)
	{
		if (left.Major != right.Major) return left.Major.CompareTo(right.Major);
		if (left.Minor != right.Minor) return left.Minor.CompareTo(right.Minor);
		if (left.Patch != right.Patch) return left.Patch.CompareTo(right.Patch);

		var leftPre = left.Prerelease ?? [];
		var rightPre = right.Prerelease ?? [];

		if (leftPre.Length == 0 && rightPre.Length == 0) return 0;

		// 1.0.0 is newer than 1.0.0-rc.1, which is the case this whole type exists for.
		if (leftPre.Length == 0) return 1;
		if (rightPre.Length == 0) return -1;

		for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
		{
			var comparison = CompareIdentifier(leftPre[i], rightPre[i]);
			if (comparison != 0) return comparison;
		}

		// rc.1 precedes rc.1.1: a larger set of identifiers wins when every shared one is equal.
		return leftPre.Length.CompareTo(rightPre.Length);
	}

	private static int CompareIdentifier(string left, string right)
	{
		var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue);
		var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue);

		if (leftNumeric && rightNumeric) return leftValue.CompareTo(rightValue);

		// "Numeric identifiers always have lower precedence than non-numeric identifiers", which is
		// what puts 1.0.0-alpha.1 below 1.0.0-alpha.beta rather than sorting it as text.
		if (leftNumeric) return -1;
		if (rightNumeric) return 1;

		return string.CompareOrdinal(left, right);
	}

	public override string ToString() => Prerelease is { Length: > 0 }
		? $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}"
		: $"{Major}.{Minor}.{Patch}";
}
