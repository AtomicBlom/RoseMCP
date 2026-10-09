namespace RoseMcp.Broker;

/// <summary>
/// Starts work that outlives the call it is started from, on an execution context of its own.
/// <para>
/// Everything started from inside a tool call inherits that call's execution context, and with it
/// every ambient the broker sets for the call: its correlation id, its origin directory, its session.
/// A poll loop, a sweep, or a child's transport -- whose read loop then handles every later call's
/// replies -- would go on carrying all three for as long as it runs, so a line written about one call
/// would be filed under another, and a hop made for nobody would claim to be made for whoever
/// happened to start the loop. Work started here inherits nothing, which is the truth about it.
/// </para>
/// <para>
/// Not for work that has to start synchronously: this always hands the work to the thread pool, so
/// anything the caller relies on being done before it returns has to be done before calling this.
/// </para>
/// </summary>
public static class Detached
{
	/// <summary>Runs <paramref name="work"/> on the thread pool with no execution context of the caller's.</summary>
	public static Task Run(Func<Task> work)
	{
		if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);

		using (ExecutionContext.SuppressFlow())
		{
			return Task.Run(work);
		}
	}

	/// <summary>Runs <paramref name="work"/> on the thread pool with no execution context of the caller's.</summary>
	public static Task<T> Run<T>(Func<Task<T>> work)
	{
		if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);

		using (ExecutionContext.SuppressFlow())
		{
			return Task.Run(work);
		}
	}
}
