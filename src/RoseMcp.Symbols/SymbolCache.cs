namespace RoseMcp.Symbols;

/// <summary>
/// Keeps one <see cref="ModuleSymbols"/> per module file, so reading a stack does not re-read the
/// same assembly once per frame.
/// <para>
/// The saving is not marginal. Naming a method costs a metadata read, and so does naming a local, so
/// a twenty-frame walk with locals opened and parsed the same handful of files around forty times.
/// Every one of those is a file read on the path a person is waiting on.
/// </para>
/// <para>
/// An entry is dropped when the file's write time or length changes. A loaded module cannot be
/// rebuilt underneath a running target, but this cache outlives targets: the next session may be
/// debugging a build made since, and answering it from the last one would name lines out of code
/// that has been replaced.
/// </para>
/// </summary>
public sealed class SymbolCache : IDisposable
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The cache the debugger uses. Shared because the alternative is a cache per session, and two
	/// sessions over one solution read the same assemblies.
	/// </summary>
	public static SymbolCache Shared { get; } = new();

	/// <summary>
	/// The symbols for a module, reading them if this is the first ask or the file has changed since
	/// the last one. Null when the file is not a managed assembly, or is gone.
	/// </summary>
	public ModuleSymbols? For(string modulePath)
	{
		var stamp = StampOf(modulePath);

		lock (_gate)
		{
			if (_entries.TryGetValue(modulePath, out var entry))
			{
				if (entry.Stamp == stamp) return entry.Symbols;

				// A different file under the same name. What was read describes a build that is gone.
				entry.Symbols?.Dispose();
				_entries.Remove(modulePath);
			}

			var symbols = ModuleSymbols.TryLoad(modulePath);

			// Cached even when it failed, so a path that is not an assembly is not re-read on every
			// frame of every stack. The stamp still governs: if the file appears later, it is read.
			_entries[modulePath] = new Entry(stamp, symbols);

			return symbols;
		}
	}

	/// <summary>Forgets one module, for a caller that knows its output has just been replaced.</summary>
	public void Evict(string modulePath)
	{
		lock (_gate)
		{
			if (!_entries.Remove(modulePath, out var entry)) return;

			entry.Symbols?.Dispose();
		}
	}

	/// <summary>
	/// What identifies this version of the file: its write time and length. A file that is missing
	/// stamps as absent, which is distinct from any real file and so re-reads if it appears.
	/// </summary>
	private static (DateTime Written, long Length) StampOf(string modulePath)
	{
		try
		{
			var file = new FileInfo(modulePath);

			return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (DateTime.MinValue, -1);
		}
		catch (Exception)
		{
			return (DateTime.MinValue, -1);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			foreach (var entry in _entries.Values)
			{
				entry.Symbols?.Dispose();
			}

			_entries.Clear();
		}
	}

	private sealed record Entry((DateTime Written, long Length) Stamp, ModuleSymbols? Symbols);
}
