namespace RoseMcp.Contracts;

/// <summary>
/// Which XAML provider sandbox folders a starting live-app host may delete: those whose host is gone.
/// <para>
/// A folder is named after the pid of the host that made it, and a pid alone does not say whether that
/// host is still running, because Windows reuses them. So a folder's owner is identified the way a
/// process is: by its id <em>and</em> when it started. A process holding the id that started after the
/// folder was made cannot be the host that made it -- two processes cannot hold one id at once -- and
/// without that test a folder whose id a long-running service picked up stays until the service exits,
/// which can be weeks. Every such folder holds a copy of the provider and a grant to ALL APPLICATION
/// PACKAGES.
/// </para>
/// <para>
/// Each folder is decided on its own, and a folder that cannot be asked about costs only itself. The
/// failure this exists to prevent is one bad entry abandoning every folder after it, on every sweep:
/// the process API answers a pid owned by a protected service with an access-denied exception rather
/// than an answer, and a guard around the whole loop turned that into a sweep that never reached the
/// rest.
/// </para>
/// <para>
/// Here rather than beside the host because no test can see inside the host: <c>RoseMcp.LiveApp</c>
/// is <c>net10.0-windows</c> and both test projects reference it without an output assembly. The host
/// keeps the part that touches the machine -- listing the folders, asking the process table, deleting.
/// </para>
/// </summary>
public static class SandboxSweep
{
	/// <summary>
	/// The folders out of <paramref name="folders"/> whose host is gone, and how many of the rest were
	/// kept because their host is running or because nothing could be learned about it.
	/// </summary>
	/// <param name="folders">Each folder under the sandbox root, by full path.</param>
	/// <param name="ownProcessId">The sweeping host's id. Its own folder is left for it to deal with.</param>
	/// <param name="createdUtc">When a folder was made. May throw; the folder is then kept.</param>
	/// <param name="process">What holds the id a folder is named after. May throw; the folder is then kept.</param>
	public static SandboxSweepPlan Plan(
		IEnumerable<string> folders,
		int ownProcessId,
		Func<string, DateTime> createdUtc,
		Func<int, SandboxProcess> process)
	{
		var stale = new List<string>();
		var running = 0;
		var undecided = 0;

		foreach (var folder in folders)
		{
			if (!int.TryParse(Path.GetFileName(folder), out var pid)) continue;
			if (pid == ownProcessId) continue;

			switch (Decide(folder, pid, createdUtc, process))
			{
				case true:
					stale.Add(folder);
					break;
				case false:
					running++;
					break;
				case null:
					undecided++;
					break;
			}
		}

		return new SandboxSweepPlan(stale, running, undecided);
	}

	/// <summary>True when the folder's host is gone, false when it is running, null when neither can be told.</summary>
	/// <remarks>
	/// Anything that throws makes the answer null, which keeps the folder. That is the safe direction:
	/// deleting a live host's folder pulls the provider out from under it, where keeping a dead one
	/// costs a folder until a later sweep can tell.
	/// </remarks>
	private static bool? Decide(string folder, int pid, Func<string, DateTime> createdUtc, Func<int, SandboxProcess> process)
	{
		SandboxProcess holder;
		try
		{
			holder = process(pid);
		}
		catch (Exception)
		{
			return null;
		}

		switch (holder.State)
		{
			case SandboxProcessState.None:
				return true;

			// Not this user's process, so not the host: a host that made a folder in this user's temp
			// directory runs as this user, and a same-user process always answers a limited query.
			case SandboxProcessState.Inaccessible:
				return true;

			case SandboxProcessState.Started:
				try
				{
					return holder.Started.ToUniversalTime() > createdUtc(folder).ToUniversalTime();
				}
				catch (Exception)
				{
					return null;
				}

			default:
				return null;
		}
	}
}

/// <summary>What a sweep decided, for the host to act on and to report.</summary>
/// <param name="Stale">The folders whose host is gone, to delete.</param>
/// <param name="Running">How many were kept because the host that made them is still running.</param>
/// <param name="Undecided">How many were kept because nothing could be learned about their host.</param>
public sealed record SandboxSweepPlan(IReadOnlyList<string> Stale, int Running, int Undecided);

/// <summary>What the process table says about one id.</summary>
public enum SandboxProcessState
{
	/// <summary>Nothing holds the id, or what held it has exited.</summary>
	None,

	/// <summary>A process holds the id, and <see cref="SandboxProcess.Started"/> says when it started.</summary>
	Started,

	/// <summary>A process holds the id and refuses even a limited query, so it is not this user's.</summary>
	Inaccessible,
}

/// <summary>What holds the id a sandbox folder is named after.</summary>
/// <param name="State">Whether anything holds it, and whether it could be asked.</param>
/// <param name="Started">
/// When it started, for <see cref="SandboxProcessState.Started"/>. Compared as an instant whatever its
/// kind, because the process API answers in local time and the file system in UTC, and a comparison of
/// the two as they stand is off by the machine's offset -- which reads every running host as a reused id.
/// </param>
public readonly record struct SandboxProcess(SandboxProcessState State, DateTime Started = default)
{
	/// <summary>Nothing holds the id.</summary>
	public static SandboxProcess None { get; } = new(SandboxProcessState.None);

	/// <summary>Something holds the id and will not say when it started.</summary>
	public static SandboxProcess Inaccessible { get; } = new(SandboxProcessState.Inaccessible);

	/// <summary>Something holds the id, and started at <paramref name="started"/>.</summary>
	public static SandboxProcess StartedAt(DateTime started) => new(SandboxProcessState.Started, started);
}
