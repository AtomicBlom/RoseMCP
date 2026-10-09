using Core;

namespace App;

/// <summary>Keeps things in a file: the other project's implementation of both interfaces.</summary>
public sealed class FileStore : IStore, IDisposable
{
	public string Name => "file";

	public void Dispose()
	{
	}
}

/// <summary>Sets the property, so it has a reference as well as a declaration.</summary>
public static class Stores
{
	public static IStore Small() => new MemoryStore { Capacity = 4 };
}
