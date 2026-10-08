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
