using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace RoseMcp.Worker;

/// <summary>
/// Loads analyzer and source generator assemblies from throwaway copies, so the originals stay
/// writable.
/// <para>
/// Without this a warm worker is a permanent lock on every analyzer in the solution. Loading an
/// assembly holds its file open for the life of the process, and this process is meant to live for
/// hours, so rebuilding an in-solution generator fails with MSB3021 "the process cannot access the
/// file". That turns the warm workspace from a benefit into an obstacle: the agent cannot rebuild
/// the very generator it is working on.
/// </para>
/// <para>
/// Roslyn shadow-copies for exactly this reason but keeps its loader internal, and
/// IAnalyzerAssemblyLoader is only two methods, so this implements them.
/// </para>
/// </summary>
public sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
	private readonly ConcurrentDictionary<string, string> _shadowByOriginal = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, string> _shadowBySimpleName = new(StringComparer.OrdinalIgnoreCase);
	private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> _resolver;
	private readonly ILogger _logger;
	private readonly string _root;
	private readonly HashSet<string> _failures = [];

	public ShadowCopyAnalyzerAssemblyLoader(ILogger<ShadowCopyAnalyzerAssemblyLoader> logger)
	{
		_logger = logger;
		_root = Path.Combine(Path.GetTempPath(), "RoseMcp", "analyzers", Environment.ProcessId.ToString());
		Directory.CreateDirectory(_root);

		// Dependencies are resolved by simple name from the copies, never from the originals, or the
		// lock we just avoided comes straight back through the side door.
		_resolver = (_, name) => name.Name is { } simpleName
			&& _shadowBySimpleName.TryGetValue(simpleName, out var path)
				? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
				: null;

		AssemblyLoadContext.Default.Resolving += _resolver;

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
	public IReadOnlyList<string> LoadFailures
	{
		get { lock (_failures) { return [.. _failures]; } }
	}

	/// <summary>Records a failure raised by AnalyzerFileReference.AnalyzerLoadFailed.</summary>
	public void RecordLoadFailure(string path, string message)
	{
		var text = $"{Path.GetFileName(path)}: {message}";

		lock (_failures)
		{
			if (!_failures.Add(text)) return;
		}

		_logger.LogWarning("Analyzer assembly failed to load -- {Failure}", text);
	}

	/// <summary>Where the copies live. Exposed so a test can prove the originals are not what got loaded.</summary>
	public string ShadowDirectory => _root;

	public void AddDependencyLocation(string fullPath)
	{
		if (string.IsNullOrEmpty(fullPath)) return;

		Shadow(fullPath);
	}

	public Assembly LoadFromPath(string fullPath)
	{
		var shadow = Shadow(fullPath) ?? fullPath;

		try
		{
			return AssemblyLoadContext.Default.LoadFromAssemblyPath(shadow);
		}
		catch (FileLoadException)
		{
			// Already loaded from somewhere else in this process. Reuse it rather than reporting a
			// load failure, which would surface as an analyzer that mysteriously produces nothing.
			var name = AssemblyName.GetAssemblyName(shadow);
			return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
		}
	}

	/// <summary>
	/// Copies a file into the shadow area, once per process.
	/// <para>
	/// The destination is derived from the original path and its timestamp rather than being unique
	/// per loader instance, and that matters: a runtime cannot hold two copies of the same assembly
	/// identity loaded from two different paths. Instance-unique directories made the second loader
	/// in a process fail to load an analyzer the first had already loaded -- and fail silently, as
	/// zero generators. Deterministic paths mean every loader agrees on one copy.
	/// </para>
	/// </summary>
	private string? Shadow(string fullPath)
	{
		if (_shadowByOriginal.TryGetValue(fullPath, out var existing)) return existing;

		var file = new FileInfo(fullPath);
		if (!file.Exists) return null;

		try
		{
			// Timestamp and length are in the key so a rebuilt analyzer gets a fresh copy rather than
			// silently reusing the previous build.
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
			_shadowBySimpleName[Path.GetFileNameWithoutExtension(fullPath)] = shadow;

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
		AssemblyLoadContext.Default.Resolving -= _resolver;

		// The directory is deliberately left alone. It is shared by every loader in this process,
		// and whatever has been loaded stays locked until the process exits, so a worker can never
		// clean up fully after itself. The next worker to start sweeps up after dead ones.
	}
}
