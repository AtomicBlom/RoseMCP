using System.Globalization;
using System.Reflection;

namespace RoseMcp.Contracts;

/// <summary>
/// Which build a binary is: its version, the commit it was built from, whether the tree had
/// uncommitted changes, when it was compiled and, for a local build, the checkout it came from.
/// <para>
/// The version alone cannot answer that. MinVer derives it from the last tag and the height above
/// it, so two local builds of different code at the same height carry the same number, and a stale
/// worker beside a rebuilt broker passes for the same build. The commit is what tells them apart,
/// so that is what every handshake compares and what a person is shown.
/// </para>
/// <para>
/// Each part is stamped at build time by <c>Directory.Build.props</c>, and each is absent rather
/// than guessed where the build could not know it: an archive build has no commit, a machine
/// without git has no dirty flag, and a CI build has no checkout worth naming. See
/// <c>docs/decisions/a-build-is-named-by-its-commit.md</c>.
/// </para>
/// </summary>
public sealed record BuildIdentity
{
	/// <summary>The marker a handshake carries after the commit for a build with uncommitted changes.</summary>
	private const string DirtyMarker = "dirty";

	/// <summary>The version, without build metadata: <c>1.3.0</c>, or <c>0.0.0</c> where nothing stamped one.</summary>
	public required string Version { get; init; }

	/// <summary>The full commit hash the build was made from, or null where the build could not read one.</summary>
	public string? Commit { get; init; }

	/// <summary>
	/// Whether the tree had uncommitted changes, untracked files included, or null where git could
	/// not be asked. A dirty build of a commit is not that commit, and two dirty builds of one commit
	/// cannot be told apart by anything here.
	/// </summary>
	public bool? Dirty { get; init; }

	/// <summary>When the assembly was compiled, or null where it was not stamped.</summary>
	public DateTimeOffset? BuiltUtc { get; init; }

	/// <summary>
	/// The working tree a local build was made in, or null for a CI build or one made outside git.
	/// It is how a running build can ask how far its checkout has moved on since.
	/// </summary>
	public string? Checkout { get; init; }

	/// <summary>
	/// What <paramref name="assembly"/> was stamped with. Each host asks about its own assembly
	/// rather than a shared one, because they are published separately and an install that half
	/// updated is a real state.
	/// </summary>
	public static BuildIdentity Of(Assembly assembly)
	{
		var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var stamped = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Where(attribute => attribute.Key.StartsWith("RoseMcp.", StringComparison.Ordinal))
			.GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

		var fromVersion = FromHandshake(informational);
		var commit = stamped.GetValueOrDefault("RoseMcp.Commit") is { Length: > 0 } stampedCommit
			? stampedCommit
			: fromVersion.Commit;

		bool? dirty = bool.TryParse(stamped.GetValueOrDefault("RoseMcp.Dirty"), out var parsed) ? parsed : null;

		return new BuildIdentity
		{
			Version = fromVersion.Version,
			Commit = commit,
			Dirty = dirty,
			BuiltUtc = BuildTime(assembly),
			Checkout = stamped.GetValueOrDefault("RoseMcp.Checkout") is { Length: > 0 } checkout
				? Path.TrimEndingDirectorySeparator(checkout)
				: null,
		};
	}

	/// <summary>
	/// Reads what a host sent as its <c>ServerInfo.Version</c>: <c>1.3.0+&lt;commit&gt;</c>, with
	/// <c>.dirty</c> after the commit for a dirty build.
	/// <para>
	/// SemVer build metadata, so a client that only wants a version still reads one, and a host from
	/// before the commit was sent -- a bare <c>1.3.0</c> -- reads as a build whose commit is unknown
	/// rather than as a mismatch. Where a commit is present and the marker is not, the build is read
	/// as clean, since a handshake cannot carry "unknown" without a marker every reader would have to
	/// learn.
	/// </para>
	/// </summary>
	public static BuildIdentity FromHandshake(string? reported)
	{
		if (string.IsNullOrWhiteSpace(reported)) return new BuildIdentity { Version = "0.0.0" };

		var plus = reported.IndexOf('+', StringComparison.Ordinal);
		var version = (plus < 0 ? reported : reported[..plus]).Trim();
		var metadata = plus < 0 ? [] : reported[(plus + 1)..].Split('.', StringSplitOptions.TrimEntries);

		var commit = metadata.FirstOrDefault(IsCommit);

		return new BuildIdentity
		{
			Version = version.Length == 0 ? "0.0.0" : version,
			Commit = commit?.ToLowerInvariant(),
			Dirty = commit is null ? null : metadata.Contains(DirtyMarker, StringComparer.OrdinalIgnoreCase),
		};
	}

	/// <summary>
	/// What a host sends as its <c>ServerInfo.Version</c>, which <see cref="FromHandshake"/> reads
	/// back. The commit rides in the version because that is the one field every MCP handshake
	/// already carries.
	/// </summary>
	public string ToHandshake()
	{
		if (Commit is null) return Version;

		return Dirty == true ? $"{Version}+{Commit}.{DirtyMarker}" : $"{Version}+{Commit}";
	}

	/// <summary>The commit as people write it, seven characters, or null where it is unknown.</summary>
	public string? ShortCommit() => Commit is { Length: > 7 } commit ? commit[..7] : Commit;

	/// <summary>
	/// The build in a few words, for a sentence: <c>1.3.0 at c11d75a</c>, with uncommitted changes
	/// said where there were any, and an unknown commit said rather than left out.
	/// </summary>
	public string Describe()
	{
		if (ShortCommit() is not { } commit) return $"{Version}, commit unknown";

		return Dirty == true ? $"{Version} at {commit} with uncommitted changes" : $"{Version} at {commit}";
	}

	/// <summary>
	/// Whether <paramref name="other"/> is this build, as far as the two can say.
	/// <para>
	/// Commits decide where both have one, and a dirty flag both know decides between builds of one
	/// commit. Where either lacks a commit the versions are all there is to compare, which is right
	/// for an older host and for two archive builds alike.
	/// </para>
	/// </summary>
	public bool IsSameBuildAs(BuildIdentity other)
	{
		var bothHaveCommits = Commit is not null && other.Commit is not null;
		if (!bothHaveCommits) return string.Equals(Version, other.Version, StringComparison.Ordinal);

		if (!string.Equals(Commit, other.Commit, StringComparison.OrdinalIgnoreCase)) return false;

		var bothKnowDirty = Dirty is not null && other.Dirty is not null;

		return !bothKnowDirty || Dirty == other.Dirty;
	}

	/// <summary>
	/// What to say about <paramref name="theirs"/> differing from <paramref name="ours"/>, or null
	/// where they are the same build. Names both, because the sentence is only useful if it says
	/// which side is which.
	/// </summary>
	/// <param name="who">What the other side is, as the start of a sentence: "The broker".</param>
	/// <param name="theirs">The other side's build.</param>
	/// <param name="ours">This process's build.</param>
	public static string? Mismatch(string who, BuildIdentity theirs, BuildIdentity ours)
	{
		if (theirs.IsSameBuildAs(ours)) return null;

		return $"{who} is build {theirs.Describe()}, where this one is {ours.Describe()}.";
	}

	/// <summary>A full hash: forty hex digits for SHA-1, sixty-four for a SHA-256 repository.</summary>
	private static bool IsCommit(string identifier) =>
		identifier.Length is 40 or 64 && identifier.All(Uri.IsHexDigit);

	/// <summary>
	/// The time stamped as a resource rather than an attribute, so a compile does not change the
	/// reference assembly and recompile everything above it. A dynamic assembly has no resources to
	/// read, and asking one throws.
	/// </summary>
	private static DateTimeOffset? BuildTime(Assembly assembly)
	{
		if (assembly.IsDynamic) return null;

		using var stream = assembly.GetManifestResourceStream("RoseMcp.BuildTime");
		if (stream is null) return null;

		using var reader = new StreamReader(stream);
		var text = reader.ReadToEnd().Trim();

		return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var built)
			? built
			: null;
	}
}
