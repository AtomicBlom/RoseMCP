using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoseMcp.Symbols;

/// <summary>
/// One module's metadata and, where there is one, its portable PDB.
/// <para>
/// The file is read into memory and closed rather than kept open, and that is not tidiness. This
/// runs inside a process that lives for hours beside a developer who is rebuilding: a held handle on
/// their output means their next build fails, which is the same failure the analyzer loader
/// shadow-copies to avoid.
/// </para>
/// </summary>
public sealed class ModuleSymbols : IDisposable
{
	private readonly PEReader _pe;

	private ModuleSymbols(PEReader pe, MetadataReader metadata, PortablePdb? pdb, PdbState state, string? problem)
	{
		_pe = pe;
		Metadata = metadata;
		Pdb = pdb;
		PdbState = state;
		PdbProblem = problem;
	}

	/// <summary>The module's own metadata: types, methods, fields.</summary>
	public MetadataReader Metadata { get; }

	/// <summary>The symbols, or null when there are none to be had. <see cref="PdbState"/> says why.</summary>
	public PortablePdb? Pdb { get; }

	public PdbState PdbState { get; }

	/// <summary>What went wrong with the symbols, when something did. Null when they loaded.</summary>
	public string? PdbProblem { get; init; }

	/// <summary>
	/// Reads a module and its symbols, or null when the module itself cannot be read.
	/// <para>
	/// Symbols failing is not the module failing: a module with no PDB is still worth reading for
	/// tokens and names, so that case comes back with <see cref="PdbState.NotFound"/> rather than
	/// null. Null means the file is not a managed assembly, or is gone.
	/// </para>
	/// </summary>
	/// <param name="modulePath">The assembly on disk.</param>
	/// <param name="pdbStreams">
	/// How to open a PDB the module names, for a test that wants to supply one. The default reads it
	/// from disk.
	/// </param>
	public static ModuleSymbols? TryLoad(string modulePath, Func<string, Stream?>? pdbStreams = null)
	{
		PEReader? pe = null;

		try
		{
			// Prefetched, so the whole image is in memory and the file handle is released here rather
			// than living as long as this object does.
			using var file = File.OpenRead(modulePath);
			pe = new PEReader(file, PEStreamOptions.PrefetchEntireImage);

			var metadata = pe.GetMetadataReader();
			var (pdb, state, problem) = OpenSymbols(modulePath, pe, pdbStreams);

			return new ModuleSymbols(pe, metadata, pdb, state, problem);
		}
		catch (Exception)
		{
			pe?.Dispose();
			return null;
		}
	}

	/// <summary>
	/// Finds and opens the module's symbols.
	/// <para>
	/// <c>TryOpenAssociatedPortablePdb</c> does the work worth not writing twice: it handles a PDB
	/// embedded in the module, follows the CodeView entry to one beside it, and -- the part that
	/// matters most -- checks the PDB's own id against the module's debug directory, so a PDB left
	/// over from an earlier build is refused rather than believed. That check is why symbols are read
	/// through it rather than by opening the file next door.
	/// </para>
	/// </summary>
	private static (PortablePdb? Pdb, PdbState State, string? Problem) OpenSymbols(
		string modulePath,
		PEReader pe,
		Func<string, Stream?>? pdbStreams)
	{
		try
		{
			var open = pdbStreams ?? FromDisk;

			if (!pe.TryOpenAssociatedPortablePdb(modulePath, open, out var provider, out var pdbPath)
				|| provider is null)
			{
				// Either the module names no PDB, or it names one whose identity does not match. The
				// two are told apart by whether a file is there at all.
				var named = NamedPdbPath(pe, modulePath);
				var exists = named is not null && File.Exists(named);

				return exists
					? (null, PdbState.Mismatched,
						$"{named} does not belong to this build of {Path.GetFileName(modulePath)}. Local names and "
							+ "line numbers from it would describe code that is not running, so they are not used.")
					: (null, PdbState.NotFound, null);
			}

			return (PortablePdb.Over(provider, pdbPath), PdbState.Loaded, null);
		}
		catch (Exception exception)
		{
			return (null, PdbState.Unreadable, exception.Message);
		}
	}

	/// <summary>
	/// The PDB path the module's own debug directory names, for telling a missing PDB from a
	/// mismatched one. Null when it names none.
	/// </summary>
	private static string? NamedPdbPath(PEReader pe, string modulePath)
	{
		foreach (var entry in pe.ReadDebugDirectory())
		{
			if (entry.Type != DebugDirectoryEntryType.CodeView) continue;

			var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
			var directory = Path.GetDirectoryName(modulePath);

			// The recorded path is the build machine's. Only its file name is any use here, and a
			// PDB beside the module is where a copied output puts one.
			return directory is null
				? Path.GetFileName(codeView.Path)
				: Path.Combine(directory, Path.GetFileName(codeView.Path));
		}

		return null;
	}

	private static Stream? FromDisk(string path)
	{
		try
		{
			// Copied into memory for the same reason the module is: nothing here holds a handle on a
			// developer's build output.
			var bytes = File.ReadAllBytes(path);

			return new MemoryStream(bytes, writable: false);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public void Dispose()
	{
		Pdb?.Dispose();
		_pe.Dispose();
	}
}
