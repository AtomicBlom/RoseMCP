namespace Core;

/// <summary>Somewhere to keep things, implemented in both projects so narrowing to one leaves the other out.</summary>
public interface IStore
{
	string Name { get; }
}

/// <summary>Keeps things in memory. Compiled once per framework this project targets.</summary>
public sealed class MemoryStore : IStore, IDisposable
{
	public string Name => "memory";

	/// <summary>An automatic property: its accessors and its backing field are symbols of their own, declared inside it.</summary>
	public int Capacity { get; init; }

	public void Dispose()
	{
	}
}

#if !NET10_0_OR_GREATER
/// <summary>Compiled only for the older framework, so one framework of this project implements IStore once more than the other.</summary>
public sealed class LegacyStore : IStore
{
	public string Name => "legacy";
}
#endif

/// <summary>
/// What a store is called. Used here, where each framework's copy reaches the one use, and in a file
/// App and App.Tests both compile, where the one line is a use in each project.
/// </summary>
public static class Labels
{
	public static string Of(IStore store) => store.Name;

	public static string OfMemory() => Of(new MemoryStore());
}
