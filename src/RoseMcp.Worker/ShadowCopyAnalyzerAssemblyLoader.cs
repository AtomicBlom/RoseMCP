using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// Loads analyzer and source generator assemblies from throwaway copies, so the originals stay
/// writable, and keeps each analyzer directory in a load context of its own, so two versions of one
/// generator can both load.
/// <para>
/// Without the copies a warm worker is a permanent lock on every analyzer in the solution. Loading
/// an assembly holds its file open for the life of the process, and this process is meant to live
/// for hours, so rebuilding an in-solution generator fails with MSB3021 "the process cannot access
/// the file". That turns the warm workspace from a benefit into an obstacle: the agent cannot
/// rebuild the very generator it is working on.
/// </para>
/// <para>
/// Without the separate contexts a solution spanning several target frameworks cannot load at all.
/// One 96-project solution carries four versions of <c>Microsoft.Extensions.Logging.Generators</c>
/// -- one per framework its projects target -- and a package ships the same version again under
/// <c>roslyn3.11</c> and <c>roslyn4.x</c>, so identical identities arrive from different files. A
/// single context holds one assembly per identity, so the second of each pair fails to load, and an
/// analyzer that fails to load is not an error: it is zero generators, silently, while MSBuild goes
/// on passing the generator to the compiler.
/// </para>
/// <para>
/// Roslyn shadow-copies and isolates per directory for exactly these reasons but keeps its loader
/// internal -- only <see cref="IAnalyzerAssemblyLoader"/> is public -- and that interface is two
/// methods, so this implements them.
/// </para>
/// </summary>
public sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
	private readonly ConcurrentDictionary<string, string> _shadowByOriginal = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, AnalyzerLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, AssemblyName?> _identities = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger _logger;
	private readonly string _root;
	private readonly List<AnalyzerLoadFailure> _failures = [];
	private readonly HashSet<string> _reported = [];

	public ShadowCopyAnalyzerAssemblyLoader(ILogger<ShadowCopyAnalyzerAssemblyLoader> logger)
	{
		_logger = logger;
		_root = Path.Combine(Path.GetTempPath(), "RoseMcp", "analyzers", Environment.ProcessId.ToString());
		Directory.CreateDirectory(_root);

		CleanUpAbandonedCopies();
	}

	/// <summary>
	/// Assemblies that would not load, and why.
	/// <para>
	/// AnalyzerFileReference reports load failures through an event and then quietly returns no
	/// analyzers, which is the same silent-nothing outcome this whole project exists to catch. These
	/// are collected so they can be reported as a degraded load instead.
	/// </para>
	/// </summary>
	/// <remarks>
	/// In the order the failures arrived. A list rather than the set that dedupes them, because the
	/// report folds these by assembly and first-appearance order is what keeps the one unusual
	/// failure from sorting underneath a family of routine ones.
	/// </remarks>
	public IReadOnlyList<AnalyzerLoadFailure> LoadFailures
	{
		get { lock (_failures) { return [.. _failures]; } }
	}

	/// <summary>Records a failure raised by AnalyzerFileReference.AnalyzerLoadFailed.</summary>
	public void RecordLoadFailure(string path, string message)
	{
		var assembly = Path.GetFileName(path);

		lock (_failures)
		{
			// Assembly and message together: several versions of one generator can still fail
			// differently for reasons of their own, and a dedupe on the name alone would report the
			// first and hide the rest.
			if (!_reported.Add($"{assembly}: {message}")) return;

			_failures.Add(new AnalyzerLoadFailure { Assembly = assembly, Message = message });
		}

		_logger.LogWarning("Analyzer assembly failed to load -- {Assembly}: {Message}", assembly, message);
	}

	/// <summary>Where the copies live. Exposed so a test can prove the originals are not what got loaded.</summary>
	public string ShadowDirectory => _root;

	public void AddDependencyLocation(string fullPath)
	{
		if (string.IsNullOrEmpty(fullPath)) return;

		if (Shadow(fullPath) is { } shadow) ContextFor(fullPath).Add(fullPath, shadow);
	}

	/// <summary>
	/// A copy of the assembly of this name sitting in <paramref name="directory"/>, shadowed on the
	/// spot if it has not been already.
	/// <para>
	/// The directory is what a load context is built around, so what is in it resolves whether or not
	/// anything declared it. Roslyn probes the directory for the same reason: the set MSBuild passes
	/// as analyzers is the set of entry points, not the closure of what they call.
	/// </para>
	/// </summary>
	private string? ShadowBeside(string directory, string simpleName, string? culture)
	{
		var candidate = string.IsNullOrEmpty(culture)
			? Path.Combine(directory, $"{simpleName}.dll")
			: Path.Combine(directory, culture, $"{simpleName}.dll");

		return File.Exists(candidate) ? Shadow(candidate) : null;
	}

	public Assembly LoadFromPath(string fullPath)
	{
		var shadow = Shadow(fullPath) ?? fullPath;
		var context = ContextFor(fullPath);

		// Registering what we are about to load means the next assembly in this directory that calls
		// into it resolves it without touching disk again.
		if (shadow != fullPath) context.Add(fullPath, shadow);

		try
		{
			return context.LoadFromAssemblyPath(shadow);
		}
		catch (FileLoadException)
		{
			// Two files in one analyzer directory carrying the same identity. Reusing what is already
			// there is right here in a way it would not be across directories: the context holds only
			// this directory's copies, so the assembly reused is one of the two files asked for rather
			// than some other project's version of the same generator.
			return context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(shadow));
		}
	}

	/// <summary>
	/// The load context that owns an analyzer file, created on first use.
	/// <para>
	/// One per directory, which is the unit Roslyn isolates on and the unit NuGet lays packages out
	/// in: a directory holds one file per name, so a context built from it holds one version of each
	/// assembly, and the versions a solution's other target frameworks want are somebody else's
	/// directory and somebody else's context.
	/// </para>
	/// </summary>
	private AnalyzerLoadContext ContextFor(string fullPath)
	{
		var directory = OwningDirectory(fullPath);

		return _contexts.GetOrAdd(directory, key => new AnalyzerLoadContext(key, this));
	}

	/// <summary>
	/// The analyzer directory a file belongs to.
	/// <para>
	/// A satellite assembly sits in a culture folder beside the assembly whose strings it carries, so
	/// its owner is the parent. Keyed on the culture folder it sits in, every translation would get a
	/// context to itself and none of them would be found from the analyzer that needs them.
	/// </para>
	/// </summary>
	private static string OwningDirectory(string fullPath)
	{
		var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;

		return IsSatellite(fullPath) ? Path.GetDirectoryName(directory) ?? directory : directory;
	}

	private static bool IsSatellite(string fullPath) =>
		fullPath.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// A copy of an assembly some other analyzer directory brought in, when its identity matches
	/// exactly.
	/// <para>
	/// A package can put an analyzer in one folder and the library it calls in another, and nothing
	/// in the analyzer's own directory would resolve that. Matching the version as well as the name
	/// is what separates this from a guess: handing back a different version of the right name is how
	/// a generator ends up loaded against an API it was not built for, and the runtime rejects it
	/// with a manifest mismatch that names the version it wanted rather than the one it got.
	/// </para>
	/// </summary>
	private string? ResolveAcrossDirectories(AssemblyName wanted)
	{
		if (wanted.Name is not { Length: > 0 } simpleName) return null;

		var candidates = new List<(Version? Version, string Path)>();

		foreach (var shadow in _shadowByOriginal.Values)
		{
			if (!Path.GetFileNameWithoutExtension(shadow).Equals(simpleName, StringComparison.OrdinalIgnoreCase)) continue;

			var identity = _identities.GetOrAdd(shadow, TryReadIdentity);
			if (identity is null) continue;
			if (!string.Equals(identity.CultureName ?? string.Empty, wanted.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) continue;

			if (identity.Version == wanted.Version) return shadow;

			candidates.Add((identity.Version, shadow));
		}

		// Nothing carries the version asked for, so the nearest one above it stands in, the way the
		// runtime would unify a reference to a newer assembly. Never one below: those are the members
		// the caller compiled against and this one does not have, and a MissingMethodException from
		// inside a generator is a long way from the version that is actually missing.
		return candidates
			.Where(candidate => wanted.Version is null || candidate.Version >= wanted.Version)
			.OrderBy(candidate => candidate.Version)
			.Select(candidate => candidate.Path)
			.FirstOrDefault();
	}

	private static AssemblyName? TryReadIdentity(string path)
	{
		try
		{
			return AssemblyName.GetAssemblyName(path);
		}
		catch (Exception exception) when (exception is BadImageFormatException or FileNotFoundException or IOException)
		{
			// A native dependency sitting beside the managed ones. Not a candidate, not a problem.
			return null;
		}
	}

	/// <summary>
	/// Copies a file into the shadow area, once per process.
	/// <para>
	/// The destination is derived from the original path and its timestamp rather than being unique
	/// per loader instance, so that several loaders in one process agree on one copy instead of each
	/// making its own. Timestamp and length are in the key as well, so a rebuilt analyzer gets a
	/// fresh copy rather than silently reusing the previous build.
	/// </para>
	/// </summary>
	private string? Shadow(string fullPath)
	{
		if (_shadowByOriginal.TryGetValue(fullPath, out var existing)) return existing;

		var file = new FileInfo(fullPath);
		if (!file.Exists) return null;

		try
		{
			var identity = $"{fullPath.ToLowerInvariant()}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
			var key = Convert.ToHexString(
				System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16];

			var target = Path.Combine(_root, key);
			Directory.CreateDirectory(target);

			var shadow = Path.Combine(target, file.Name);
			if (!File.Exists(shadow) || new FileInfo(shadow).Length != file.Length)
			{
				File.Copy(fullPath, shadow, overwrite: true);
			}

			_shadowByOriginal[fullPath] = shadow;

			return shadow;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Falling back to the original still works; it just holds the lock we were avoiding.
			_logger.LogWarning(exception, "Could not shadow-copy {Path}; loading it in place.", fullPath);
			return null;
		}
	}

	/// <summary>
	/// Removes copies left by workers that are no longer running. Loaded assemblies cannot be
	/// unloaded, so a worker can never fully clean up after itself on the way out.
	/// </summary>
	private void CleanUpAbandonedCopies()
	{
		var parent = Path.GetDirectoryName(_root);
		if (parent is null || !Directory.Exists(parent)) return;

		foreach (var directory in Directory.EnumerateDirectories(parent))
		{
			if (!int.TryParse(Path.GetFileName(directory), out var processId)) continue;
			if (processId == Environment.ProcessId || IsRunning(processId)) continue;

			TryDelete(directory);
		}
	}

	private static bool IsRunning(int processId)
	{
		try
		{
			using var process = System.Diagnostics.Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			return false;
		}
	}

	private void TryDelete(string directory)
	{
		try
		{
			Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogDebug(exception, "Could not remove the abandoned shadow copy at {Directory}.", directory);
		}
	}

	public void Dispose()
	{
		// The contexts stay. An AssemblyLoadContext holding analyzers cannot usefully be unloaded --
		// Roslyn keeps references to the types it found in them -- and the directory is shared by
		// every loader in this process and stays locked until the process exits, so a worker can
		// never clean up fully after itself. The next worker to start sweeps up after dead ones.
	}

	/// <summary>
	/// One analyzer directory's assemblies, isolated from every other directory's.
	/// <para>
	/// Resolution is by the identity asked for, within this directory, falling back to an exact
	/// identity match elsewhere in the solution. What it must never do is answer a request for one
	/// version with another version of the same name -- that is the collision this class exists to
	/// prevent, and it surfaces as a generator producing nothing rather than as an error.
	/// </para>
	/// </summary>
	private sealed class AnalyzerLoadContext(string directory, ShadowCopyAnalyzerAssemblyLoader owner)
		: AssemblyLoadContext($"RoseMcp analyzers: {directory}")
	{
		private readonly ConcurrentDictionary<string, string> _shadowByName = new(StringComparer.OrdinalIgnoreCase);

		public void Add(string originalPath, string shadowPath) =>
			_shadowByName[KeyFor(SimpleNameOf(originalPath), CultureOf(originalPath))] = shadowPath;

		protected override Assembly? Load(AssemblyName assemblyName)
		{
			// The compiler's own assemblies come from the host, always. An analyzer package that ships
			// its own copy of Microsoft.CodeAnalysis would otherwise load a second Roslyn in here, and
			// the generator would then be handed a GeneratorInitializationContext belonging to a type
			// it does not recognise as its own.
			if (BelongsToTheHost(assemblyName)) return null;

			if (assemblyName.Name is not { Length: > 0 } simpleName) return null;

			var key = KeyFor(simpleName, assemblyName.CultureName);

			// What MSBuild declared, then whatever else is lying in the directory, then an exact match
			// somewhere else in the solution. The probe is not a nicety: a package's analyzer calls
			// into a helper beside it that no project lists as an analyzer of its own -- the SDK's
			// CSharp.NetAnalyzers needs NetAnalyzers, and the interop generators need
			// Microsoft.Interop.SourceGeneration -- and without it those load as a fraction of their
			// types and none of their fixers.
			var path = _shadowByName.TryGetValue(key, out var declared) ? declared
				: owner.ShadowBeside(directory, simpleName, assemblyName.CultureName)
				?? owner.ResolveAcrossDirectories(assemblyName);

			// Null hands the request back to the default context, which is right for anything in the
			// shared framework and right as a last resort for everything else.
			return path is null ? null : LoadFromAssemblyPath(path);
		}

		private static bool BelongsToTheHost(AssemblyName assemblyName) =>
			assemblyName.Name is { } simpleName && CompilerAssemblies.Contains(simpleName);

		/// <summary>
		/// The compiler itself, named exactly rather than by prefix.
		/// <para>
		/// A prefix is the tempting rule and the wrong one: analyzers ship under the same prefix
		/// without being the compiler, and the SDK's own code-style fixers are the case that proves it
		/// -- <c>Microsoft.CodeAnalysis.CodeStyle.Fixes</c> depends on
		/// <c>Microsoft.CodeAnalysis.CodeStyle</c>, and sending that to a host which has never heard of
		/// it loses every fixer in the assembly rather than the one type that could not load.
		/// </para>
		/// </summary>
		private static readonly HashSet<string> CompilerAssemblies = new(StringComparer.OrdinalIgnoreCase)
		{
			"Microsoft.CodeAnalysis",
			"Microsoft.CodeAnalysis.CSharp",
			"Microsoft.CodeAnalysis.CSharp.Workspaces",
			"Microsoft.CodeAnalysis.VisualBasic",
			"Microsoft.CodeAnalysis.VisualBasic.Workspaces",
			"Microsoft.CodeAnalysis.Workspaces",
			"System.Collections.Immutable",
			"System.Reflection.Metadata",
		};

		private static string SimpleNameOf(string path) => Path.GetFileNameWithoutExtension(path);

		private static string? CultureOf(string path) =>
			IsSatellite(path) ? Path.GetFileName(Path.GetDirectoryName(path)) : null;

		private static string KeyFor(string simpleName, string? culture) =>
			string.IsNullOrEmpty(culture) ? simpleName : $"{simpleName}/{culture}";
	}
}
