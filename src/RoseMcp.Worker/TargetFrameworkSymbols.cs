using System.Text.RegularExpressions;

namespace RoseMcp.Worker;

/// <summary>
/// Works out which framework a project was compiled for from the symbols the compiler was given.
/// <para>
/// This exists because the direct answer is not always there to ask for. MSBuild publishes
/// <c>build_property.TargetFramework</c> into the analyzer config only when something has asked for
/// it, so a project with no source generator often has none -- eleven healthy netstandard2.0
/// projects in Drawboard's Revit monorepo among them. The SDK defines these symbols regardless,
/// because conditional compilation would not work otherwise.
/// </para>
/// </summary>
public static partial class TargetFrameworkSymbols
{
	/// <summary>
	/// The framework these symbols describe, or null if they describe none.
	/// <para>
	/// The SDK defines exactly one symbol naming the actual target -- <c>NET48</c>, <c>NET10_0</c>,
	/// <c>NETSTANDARD2_0</c> -- alongside an <c>_OR_GREATER</c> for that target and every one below
	/// it. Only the exact one says what this is, so the rest are skipped.
	/// </para>
	/// <para>
	/// The platform suffix of something like <c>net10.0-windows10.0.26100.0</c> is not
	/// reconstructed. It would have to be assembled from a second family of symbols, and a project
	/// that targets a platform has a generator's worth of tooling and therefore the build's own
	/// exact value already. Answering <c>net10.0</c> is honest; inventing a platform version is not.
	/// </para>
	/// </summary>
	public static string? Infer(IEnumerable<string>? preprocessorSymbols)
	{
		if (preprocessorSymbols is null) return null;

		foreach (var symbol in preprocessorSymbols)
		{
			if (symbol.EndsWith("_OR_GREATER", StringComparison.Ordinal)) continue;

			var match = ExactTarget().Match(symbol);
			if (!match.Success) continue;

			var moniker = match.Groups["moniker"].Value.ToLowerInvariant();
			var major = match.Groups["major"].Value;
			var minor = match.Groups["minor"];

			// net48 and net472 spell the version with no separator; everything since net5.0 uses one.
			return minor.Success ? $"{moniker}{major}.{minor.Value}" : $"{moniker}{major}";
		}

		return null;
	}

	/// <summary>
	/// NETSTANDARD2_0, NETCOREAPP3_1, NET10_0, NET48. Anchored so NETFRAMEWORK, NETCOREAPP and a
	/// bare NET, which name a family rather than a target, do not match.
	/// </summary>
	[GeneratedRegex(
		"^(?<moniker>NETSTANDARD|NETCOREAPP|NET)(?<major>[0-9]+)(?:_(?<minor>[0-9]+))?$",
		RegexOptions.CultureInvariant)]
	private static partial Regex ExactTarget();
}
