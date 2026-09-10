namespace RoseMcp.Contracts;

/// <summary>
/// Where a live-app host finds the native XAML provider to inject: an explicit override, the
/// published layout beside the host, or the repository's own build output.
/// <para>
/// Here rather than beside the host, and taking its directories rather than reading
/// <c>AppContext.BaseDirectory</c>, because it is the one resolver in the install layout no test
/// could reach: the host is <c>net10.0-windows</c> and neither test project takes a compile
/// reference on it. Deciding on a path and an existence check is exactly the kind of thing the
/// layout tests exist for, and exactly the kind of thing that is wrong only on an install.
/// </para>
/// </summary>
public static class XamlProviderPath
{
	/// <summary>
	/// The provider for this host's architecture, or null when none of the three places has one --
	/// which the caller reports rather than faults on, since a machine without the provider built is
	/// an ordinary state.
	/// </summary>
	/// <param name="lookup">Where to look and what for.</param>
	public static string? Resolve(XamlProviderLookup lookup)
	{
		if (!string.IsNullOrWhiteSpace(lookup.Configured) && File.Exists(lookup.Configured))
		{
			return Path.GetFullPath(lookup.Configured);
		}

		var alongside = Path.Combine(
			lookup.BaseDirectory, "xaml-provider", lookup.RuntimeIdentifier, lookup.ProviderFileName);

		if (File.Exists(alongside)) return alongside;

		// The development fallback, and it must come last: an install has the provider beside it, and
		// finding a repository's build output first would inject whatever was last compiled there.
		if (RepositoryRoot(lookup.BaseDirectory) is not { } root) return null;

		var providerBin = Path.Combine(root, "src", lookup.ProviderProjectName, "bin", lookup.Platform);
		if (!Directory.Exists(providerBin)) return null;

		return Directory.EnumerateFiles(providerBin, lookup.ProviderFileName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	/// <summary>
	/// The directory at or above <paramref name="from"/> holding the solution, or null outside a
	/// checkout -- which is every install, and is why this is a fallback rather than the answer.
	/// </summary>
	private static string? RepositoryRoot(string from)
	{
		var directory = new DirectoryInfo(from);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName;
	}
}

/// <summary>
/// What <see cref="XamlProviderPath.Resolve"/> needs to decide. A record because six positional
/// strings at a call site say nothing about which is which.
/// </summary>
/// <param name="Configured">An explicit override, which wins when it names a file that exists.</param>
/// <param name="BaseDirectory">The host's own directory, which the published layout hangs off.</param>
/// <param name="RuntimeIdentifier">The host's RID, which names its folder in that layout.</param>
/// <param name="Platform">The provider's build platform (x64 or arm64), for the repository fallback.</param>
/// <param name="ProviderFileName">The provider DLL's file name, which differs per XAML stack.</param>
/// <param name="ProviderProjectName">Its project directory under <c>src</c>, for the same fallback.</param>
public sealed record XamlProviderLookup(
	string? Configured,
	string BaseDirectory,
	string RuntimeIdentifier,
	string Platform,
	string ProviderFileName,
	string ProviderProjectName);
