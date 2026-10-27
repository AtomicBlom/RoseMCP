using RoseMcp.Tap.Protocol;

namespace RoseMcp.Contracts;

/// <summary>
/// The rules for taps dropped into RoseMCP's tap folders: which ones are admitted, and which one serves
/// a target that no in-box tap does.
/// <para>
/// The in-box taps are chosen first and by loaded modules, as <see cref="XamlStackModules"/> decides, and
/// nothing dropped in can take a framework they serve: a stray assembly naming itself WinUI would
/// otherwise quietly replace the tap a WinUI app has always been served by. Everything else is served by
/// the first admitted tap whose framework's endpoint is listening in the target, because a listening
/// endpoint is the one fact that says both "this app runs that framework" and "this app opted into
/// being inspected".
/// </para>
/// <para>
/// Here rather than beside the host because the host is <c>net10.0-windows</c> and the unit suite cannot
/// reference it; what the host contributes -- the folders it read, whether a pipe exists -- is passed in.
/// </para>
/// </summary>
public static class DroppedInTaps
{
	/// <summary>The frameworks the in-box taps serve, by the name a dropped-in tap might claim and the modules that identify them.</summary>
	private static readonly (string Name, IReadOnlyList<string> Modules)[] InBox =
	[
		("UWP", XamlStackModules.Uwp),
		("WinUI", XamlStackModules.WinUi),
	];

	/// <summary>
	/// Why <paramref name="tap"/> is refused, or null to admit it beside <paramref name="admitted"/>. A tap
	/// is refused when it names no framework, endpoint or entry type; when its endpoint has no
	/// <c>{pid}</c>, so every app would share one name; when it claims a framework an in-box tap serves;
	/// when it speaks a protocol this host does not accept from a dropped-in tap; or when an admitted tap
	/// already serves its framework, which the first one found keeps.
	/// </summary>
	public static string? Refuse(DroppedInTap tap, IReadOnlyList<DroppedInTap> admitted)
	{
		var file = Path.GetFileName(tap.AssemblyPath);

		if (tap.Name.Length == 0 || tap.Endpoint.Length == 0 || tap.EntryType.Length == 0)
		{
			return $"{file} is marked as a tap but does not say {Missing(tap)}, so it cannot be reached.";
		}

		if (!tap.Endpoint.Contains("{pid}", StringComparison.Ordinal))
		{
			return $"The {tap.Name} tap in {file} names the endpoint '{tap.Endpoint}' with no {{pid}} in it, so every app "
				+ "would listen on the same pipe and there would be no telling them apart.";
		}

		foreach (var (name, modules) in InBox)
		{
			var claimsName = string.Equals(tap.Name, name, StringComparison.OrdinalIgnoreCase);
			var claimsModule = modules.Any(module => string.Equals(module, tap.Module, StringComparison.OrdinalIgnoreCase));
			if (claimsName || claimsModule)
			{
				return $"The {tap.Name} tap in {file} claims {(claimsName ? name : tap.Module)}, which RoseMCP's own {name} tap "
					+ $"serves. A tap dropped in cannot replace one RoseMCP ships, so {file} is ignored.";
			}
		}

		if (!TapProtocolRange.DroppedIn.Contains(tap.Protocol))
		{
			return $"The {tap.Name} tap in {file} speaks protocol {tap.Protocol}, and this RoseMCP accepts {TapProtocolRange.DroppedIn} "
				+ (tap.Protocol < TapProtocolRange.DroppedIn.Minimum
					? "from a tap built apart from it. Rebuild the tap against a newer contract."
					: "from a tap built apart from it. The tap is newer than this RoseMCP, which has to be updated to use it.");
		}

		var first = admitted.FirstOrDefault(other => string.Equals(other.Name, tap.Name, StringComparison.OrdinalIgnoreCase));
		if (first is not null)
		{
			return $"The {tap.Name} tap in {tap.AssemblyPath} is ignored, because the one in {first.AssemblyPath} was found "
				+ "first and serves the same framework.";
		}

		return null;
	}

	/// <summary>
	/// The admitted tap that serves process <paramref name="pid"/>, or the sentence saying why none does.
	/// <para>
	/// Asked only where no in-box tap serves the target. <paramref name="listening"/> says whether a pipe
	/// of a given name exists. A target that has loaded a tap's framework module but has no endpoint
	/// listening is said as exactly that, because it is the ordinary state of an app built without its
	/// framework's diagnostics switched on, and "nothing serves this app" would send the reader looking for
	/// a missing tap.
	/// </para>
	/// </summary>
	/// <param name="taps">The admitted taps, in the order they were found.</param>
	/// <param name="pid">The target's process id.</param>
	/// <param name="loadedModules">The file names of the modules the target has loaded.</param>
	/// <param name="listening">Whether a pipe of the given name exists.</param>
	/// <param name="noInBoxTap">Why no in-box tap serves the target, which is the answer where nothing dropped in does either.</param>
	/// <param name="folders">The folders taps were looked for in, named when none was found.</param>
	public static (DroppedInTap? Tap, string? Reason) Choose(
		IReadOnlyList<DroppedInTap> taps,
		int pid,
		IReadOnlyList<string> loadedModules,
		Func<string, bool> listening,
		string noInBoxTap,
		IReadOnlyList<string> folders)
	{
		foreach (var tap in taps)
		{
			if (listening(EndpointWire.PipeName(tap.Endpoint, pid))) return (tap, null);
		}

		var running = taps.FirstOrDefault(tap => tap.Module.Length > 0
			&& loadedModules.Any(module => string.Equals(module, tap.Module, StringComparison.OrdinalIgnoreCase)));
		if (running is not null)
		{
			return (null, $"The target has loaded {running.Module}, which the {running.Name} tap serves, but nothing is listening "
				+ $"on {EndpointWire.PipeName(running.Endpoint, pid)}. {running.Name}'s diagnostics endpoint only runs in an app "
				+ "built with its diagnostics switched on, so this app has to be rebuilt with them, or the endpoint has not "
				+ "started yet because the app has not built its first UI.");
		}

		if (taps.Count == 0)
		{
			return (null, $"{noInBoxTap} No tap dropped into {DescribeFolders(folders)} serves it either.");
		}

		return (null, $"{noInBoxTap} None of the taps dropped in ({string.Join(", ", taps.Select(tap => tap.Name))}) has an "
			+ "endpoint listening in it either.");
	}

	private static string Missing(DroppedInTap tap) => string.Join(" or ", new[]
	{
		tap.Name.Length == 0 ? "which framework it serves" : null,
		tap.Endpoint.Length == 0 ? "which endpoint to reach it through" : null,
		tap.EntryType.Length == 0 ? "which type starts it" : null,
	}.OfType<string>());

	private static string DescribeFolders(IReadOnlyList<string> folders) => folders.Count switch
	{
		0 => "any tap folder",
		1 => folders[0],
		_ => string.Join(", ", folders),
	};
}
