using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Injects the RoseMcp.Xaml.Uwp.Tap diagnostics provider into the target and reads back what it reports (#2/#3).
/// <c>InitializeXamlDiagnosticsEx</c> (exported from Windows.UI.Xaml.dll) loads the provider into the
/// app by pid; the two ends exchange tab-separated files through a working folder this side stages and
/// grants the app's AppContainer rights to, since the provider runs sandboxed and cannot read Program
/// Files or write arbitrary paths. The provider must match the target's architecture, which is this
/// host's architecture -- an x64 provider for a classic UWP app emulated on ARM64.
/// </summary>
internal sealed class XamlDiagnosticsSession(ILogger logger)
{
	// Must match CLSID_RoseTap in RoseMcp.Xaml.Uwp.Tap.cpp.
	private static readonly Guid ProviderClsid = new("7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6");

	// The well-known diagnostics endpoint; anything else makes InitializeXamlDiagnosticsEx return
	// ERROR_NOT_FOUND (0x80070490).
	private const string EndpointName = "VisualDiagConnection1";

	private const string ProviderFileName = "RoseMcp.Xaml.Uwp.Tap.dll";
	private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(15);

	private string? _workDir;
	private string? _stagedProvider;

	/// <summary>
	/// Reads a snapshot of the target's live visual tree, injecting the provider first. Returns a tree
	/// with a <see cref="LiveXamlTree.Detail"/> and no nodes -- never throws -- when the provider is not
	/// available for this architecture, injection fails, or the target has no XAML UI.
	/// </summary>
	public LiveXamlTree ReadTree(int pid)
	{
		var (workDir, error) = Inject(pid, "tree");
		if (error is not null) return new LiveXamlTree { Detail = error };

		if (!WaitForFile(Path.Combine(workDir!, "tree.ready"), SnapshotTimeout))
		{
			return new LiveXamlTree { Detail = "The XAML provider was injected but did not produce a tree snapshot in time." };
		}

		try
		{
			var nodes = ParseTree(Path.Combine(workDir!, "tree.tsv"));
			logger.LogInformation("Read a XAML tree of {Count} element(s) from pid {Pid}.", nodes.Count, pid);
			return new LiveXamlTree { Nodes = nodes };
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML tree snapshot failed.");
			return new LiveXamlTree { Detail = $"Could not read the tree snapshot: {exception.Message}" };
		}
	}

	/// <summary>
	/// Reads one element's properties by injecting the provider with a properties request. By default
	/// only set (non-default) properties come back; <paramref name="includeDefaults"/> asks for the
	/// framework defaults too. Returns a result with a detail (and no properties) rather than throwing
	/// when the element cannot be read.
	/// </summary>
	public LiveXamlProperties ReadProperties(int pid, ulong handle, bool includeDefaults)
	{
		var request = includeDefaults ? $"properties {handle} all" : $"properties {handle}";
		var (workDir, error) = Inject(pid, request);
		if (error is not null) return new LiveXamlProperties { Handle = handle, Detail = error };

		if (!WaitForFile(Path.Combine(workDir!, "properties.ready"), SnapshotTimeout))
		{
			return new LiveXamlProperties { Handle = handle, Detail = "The XAML provider was injected but did not produce the properties in time." };
		}

		try
		{
			var properties = ParseProperties(Path.Combine(workDir!, "properties.tsv"), handle);
			logger.LogInformation("Read {Count} propert(y/ies) for handle {Handle} from pid {Pid}.", properties.Count, handle, pid);
			return properties;
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML properties failed.");
			return new LiveXamlProperties { Handle = handle, Detail = $"Could not read the properties: {exception.Message}" };
		}
	}

	/// <summary>
	/// Arms select mode (#18) from this side. The in-app toolbar is already resident -- any XAML tool
	/// installs it -- so this is the same act as pressing its Select Element button, and exists because
	/// an agent chasing a visual path to an element should not have to ask a person to press it.
	/// Confirms the overlay actually armed rather than assuming it.
	/// </summary>
	public LiveXamlSelection EnterSelectMode(int pid)
	{
		var (workDir, error) = Inject(pid, "select");
		if (error is not null) return new LiveXamlSelection { Detail = error };

		if (!WaitForFile(Path.Combine(workDir!, "select.ready"), SnapshotTimeout))
		{
			return new LiveXamlSelection { Detail = "The provider was injected but did not arm select mode (the app may have no diagnostics UI layer)." };
		}

		return new LiveXamlSelection { Armed = true, Detail = "Select mode is armed: click an element in the app, then read the selection." };
	}

	/// <summary>
	/// Reads the element that was picked, if any. Deliberately does not inject: the toolbar is resident
	/// and owns the selection, and the person may have picked without this side being involved at all --
	/// which is the case this exists for. That is also why the armed state is read from the provider's
	/// own state file rather than remembered here.
	/// </summary>
	public LiveXamlSelection ReadSelection()
	{
		if (_workDir is null)
		{
			return new LiveXamlSelection { Detail = "No XAML tool has run against this session yet, so the in-app toolbar is not installed." };
		}

		var armed = ReadOverlayMode() == "select";
		var selectionFile = Path.Combine(_workDir, "selection.tsv");
		if (!File.Exists(selectionFile))
		{
			return new LiveXamlSelection
			{
				Armed = armed,
				Detail = armed
					? "Select mode is armed; nothing has been picked yet."
					: "Nothing has been picked yet. Press Select Element on the in-app toolbar, or arm it from here.",
			};
		}

		try
		{
			foreach (var line in File.ReadLines(selectionFile, Encoding.UTF8))
			{
				var fields = line.Split('\t');
				if (fields.Length < 3 || !ulong.TryParse(fields[0], out var handle)) continue;

				var name = Unescape(fields[2]);
				return new LiveXamlSelection
				{
					Selected = true,
					Armed = armed,
					Handle = handle,
					TypeName = EmptyToNull(Unescape(fields[1])),
					Name = string.IsNullOrEmpty(name) ? null : name,
				};
			}

			return new LiveXamlSelection { Armed = armed, Detail = "The recorded selection could not be read." };
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML selection failed.");
			return new LiveXamlSelection { Armed = armed, Detail = $"Could not read the selection: {exception.Message}" };
		}
	}

	/// <summary>
	/// What the in-app toolbar says its mode is. Absent or unreadable counts as idle: the file is only
	/// ever a hint about a UI the person controls, and no tool should fail because it is missing.
	/// </summary>
	private string ReadOverlayMode()
	{
		try
		{
			var stateFile = Path.Combine(_workDir!, "overlay.state");
			return File.Exists(stateFile) ? File.ReadAllText(stateFile).Trim() : "idle";
		}
		catch (IOException)
		{
			return "idle";
		}
	}

	/// <summary>
	/// Hot-reloads the target by diffing two XAML versions and applying the edits to the live tree (#12).
	/// Property edits on named elements are applied through the provider; structural edits and
	/// unnamed-element targets are reported as not-yet-applied rather than dropped silently. Returns each
	/// computed edit with its outcome, plus the diff engine's notes.
	/// </summary>
	public LiveXamlReloadResult ApplyReload(int pid, string oldXaml, string newXaml)
	{
		XamlDiffResult diff;
		try
		{
			diff = RoseMcp.XamlDiff.XamlDiff.Compute(oldXaml, newXaml);
		}
		catch (Exception exception)
		{
			return new LiveXamlReloadResult { Detail = $"Could not diff the XAML: {exception.Message}" };
		}

		// Translate the edits the live applier supports -- property changes on named elements -- into
		// provider commands; the rest are carried through as results marked unsupported.
		var commands = new List<string>();
		var plans = new List<(XamlEdit Edit, string? Name)>();
		foreach (var edit in diff.Edits)
		{
			var name = NamedTarget(edit.Target);
			var supported = name is not null && edit.Kind is XamlEditKind.SetProperty or XamlEditKind.ClearProperty;
			plans.Add((edit, supported ? name : null));

			if (supported)
			{
				var op = edit.Kind == XamlEditKind.SetProperty ? "SetProperty" : "ClearProperty";
				commands.Add(string.Join('\t', op, name, edit.Property ?? string.Empty, edit.ValueType ?? string.Empty, edit.Value ?? string.Empty));
			}
		}

		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		if (commands.Count > 0)
		{
			var (workDir, error) = Inject(pid, "apply", commands);
			if (error is not null) return new LiveXamlReloadResult { Detail = error };

			if (!WaitForFile(Path.Combine(workDir!, "apply.ready"), SnapshotTimeout))
			{
				return new LiveXamlReloadResult { Detail = "The XAML provider was injected but did not report the apply in time." };
			}

			statuses = ParseApplyResults(Path.Combine(workDir!, "apply.tsv"));
		}

		var results = new List<LiveXamlEditResult>();
		foreach (var (edit, name) in plans)
		{
			string status;
			if (name is null)
			{
				status = edit.Kind is XamlEditKind.AddChild or XamlEditKind.RemoveChild
					? "unsupported: structural edits are not applied live yet"
					: "unsupported: unnamed-element addressing is not applied live yet";
			}
			else
			{
				var op = edit.Kind == XamlEditKind.SetProperty ? "SetProperty" : "ClearProperty";
				status = statuses.GetValueOrDefault($"{op}\t{name}\t{edit.Property}", "not reported");
			}

			results.Add(new LiveXamlEditResult
			{
				Kind = edit.Kind.ToString(),
				Target = edit.Target,
				Property = edit.Property,
				Value = edit.Value,
				Status = status,
			});
		}

		return new LiveXamlReloadResult
		{
			Applied = results.Count(result => result.Status == "applied"),
			Results = results,
			Notes = diff.Notes,
		};
	}

	/// <summary>A diff target of the form <c>#name</c> (a named element, not a path) yields its name.</summary>
	private static string? NamedTarget(string target)
		=> target.StartsWith('#') && !target.Contains('/') ? target[1..] : null;

	private static Dictionary<string, string> ParseApplyResults(string applyFile)
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in File.ReadLines(applyFile, Encoding.UTF8))
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 4) continue;

			// Keyed by op, target name, property -- the same shape the command was sent as.
			statuses[$"{fields[0]}\t{Unescape(fields[1])}\t{Unescape(fields[2])}"] = fields[3];
		}

		return statuses;
	}

	/// <summary>
	/// Stages the provider, leaves the request for it, clears any stale output, and injects. Returns the
	/// working folder, or an error string when the provider is unavailable, staging fails, or injection
	/// is rejected. Each request re-injects because the provider does its work on the app's UI thread at
	/// SetSite.
	/// </summary>
	private (string? WorkDir, string? Error) Inject(int pid, string request, IReadOnlyList<string>? commands = null)
	{
		var provider = ResolveProviderPath();
		if (provider is null)
		{
			return (null, $"The XAML provider ({ProviderFileName}) was not found for this host's architecture; build src/RoseMcp.Xaml.Uwp.Tap for {ProviderPlatform()}.");
		}

		string workDir;
		string stagedProvider;
		try
		{
			(workDir, stagedProvider) = StageSandboxFolder(provider);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Staging the XAML provider sandbox folder failed.");
			return (null, $"Could not stage the XAML provider: {exception.Message}");
		}

		foreach (var stale in new[] { "tree.tsv", "tree.ready", "properties.tsv", "properties.ready", "apply.tsv", "apply.ready", "commands.tsv" })
		{
			TryDelete(Path.Combine(workDir, stale));
		}

		// A selection outlives an injection, because the toolbar holding it does: reading the tree must
		// not throw away an element the person picked minutes ago. Only arming a fresh pick clears it.
		if (request == "select")
		{
			foreach (var stale in new[] { "select.ready", "selection.tsv", "selection.ready" })
			{
				TryDelete(Path.Combine(workDir, stale));
			}
		}

		try
		{
			if (commands is not null)
			{
				// No explicit encoding: the default is UTF-8 without a BOM, which the provider's narrow
				// command reader expects; Encoding.UTF8 would prepend a BOM and corrupt the first op.
				File.WriteAllLines(Path.Combine(workDir, "commands.tsv"), commands);
			}

			File.WriteAllText(Path.Combine(workDir, "request.txt"), request);
		}
		catch (Exception exception)
		{
			return (null, $"Could not write the provider request: {exception.Message}");
		}

		var hr = InitializeXamlDiagnosticsEx(EndpointName, (uint)pid, null, stagedProvider, ProviderClsid, workDir);
		if (hr < 0)
		{
			return (null, $"InitializeXamlDiagnosticsEx failed (0x{hr:x8}); the target may have no XAML UI or not be a packaged app.");
		}

		return (workDir, null);
	}

	private (string WorkDir, string StagedProvider) StageSandboxFolder(string provider)
	{
		// Stage once per session and reuse: the first injection loads the provider DLL into the target,
		// which holds the file open, so a later injection cannot overwrite it -- and need not, since it
		// is the same provider. Each request re-injects from this one staged copy.
		if (_workDir is not null && _stagedProvider is not null && File.Exists(_stagedProvider))
		{
			return (_workDir, _stagedProvider);
		}

		var workDir = Path.Combine(Path.GetTempPath(), "RoseMcpXaml", Environment.ProcessId.ToString());
		Directory.CreateDirectory(workDir);

		var stagedProvider = Path.Combine(workDir, ProviderFileName);
		if (!File.Exists(stagedProvider))
		{
			File.Copy(provider, stagedProvider, overwrite: true);
		}

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
		{
			Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)");
		}

		_workDir = workDir;
		_stagedProvider = stagedProvider;
		return (workDir, stagedProvider);
	}

	private void Icacls(string path, string arguments)
	{
		try
		{
			var start = new ProcessStartInfo("icacls.exe", $"\"{path}\" {arguments}")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using var process = Process.Start(start);
			process?.WaitForExit();
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "icacls {Arguments} on {Path} failed.", arguments, path);
		}
	}

	private static List<LiveXamlNode> ParseTree(string treeFile)
	{
		var nodes = new List<LiveXamlNode>();
		foreach (var line in File.ReadLines(treeFile, Encoding.UTF8))
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields.Length < 5) continue;
			if (!ulong.TryParse(fields[0], out var handle) || !ulong.TryParse(fields[1], out var parent) || !int.TryParse(fields[2], out var childIndex))
			{
				continue;
			}

			var name = Unescape(fields[4]);
			nodes.Add(new LiveXamlNode
			{
				Handle = handle,
				Parent = parent,
				ChildIndex = childIndex,
				TypeName = Unescape(fields[3]),
				Name = string.IsNullOrEmpty(name) ? null : name,
			});
		}

		return nodes;
	}

	private static LiveXamlProperties ParseProperties(string propertiesFile, ulong handle)
	{
		string? typeName = null;
		string? elementFile = null;
		int? elementLine = null;
		int? elementColumn = null;
		var properties = new List<LiveXamlProperty>();

		foreach (var line in File.ReadLines(propertiesFile, Encoding.UTF8))
		{
			if (line.Length == 0) continue;

			var fields = line.Split('\t');
			if (fields[0] == "E" && fields.Length >= 5)
			{
				typeName = EmptyToNull(Unescape(fields[1]));
				elementFile = EmptyToNull(Unescape(fields[2]));
				elementLine = ParsePositive(fields[3]);
				elementColumn = ParsePositive(fields[4]);
			}
			else if (fields[0] == "P" && fields.Length >= 10)
			{
				var isNull = fields[9] == "1";
				properties.Add(new LiveXamlProperty
				{
					Name = Unescape(fields[1]),
					Value = isNull ? null : Unescape(fields[2]),
					ValueType = EmptyToNull(Unescape(fields[3])),
					DeclaringType = EmptyToNull(Unescape(fields[4])),
					Provenance = fields[5],
					SourceFile = EmptyToNull(Unescape(fields[6])),
					SourceLine = ParsePositive(fields[7]),
					SourceColumn = ParsePositive(fields[8]),
				});
			}
		}

		return new LiveXamlProperties
		{
			Handle = handle,
			TypeName = typeName,
			SourceFile = elementFile,
			SourceLine = elementLine,
			SourceColumn = elementColumn,
			Properties = properties,
		};
	}

	private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

	private static int? ParsePositive(string field) => int.TryParse(field, out var value) && value > 0 ? value : null;

	private static string Unescape(string field)
	{
		if (field.IndexOf('\\') < 0) return field;

		var builder = new StringBuilder(field.Length);
		for (var i = 0; i < field.Length; i++)
		{
			if (field[i] == '\\' && i + 1 < field.Length)
			{
				var next = field[++i];
				builder.Append(next switch
				{
					't' => '\t',
					'r' => '\r',
					'n' => '\n',
					'\\' => '\\',
					_ => next,
				});
			}
			else
			{
				builder.Append(field[i]);
			}
		}

		return builder.ToString();
	}

	private static bool WaitForFile(string path, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(path)) return true;
			Thread.Sleep(100);
		}

		return File.Exists(path);
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path)) File.Delete(path);
		}
		catch (Exception)
		{
			// A stale file we cannot delete is handled by the ready-marker wait, not fatal here.
		}
	}

	/// <summary>
	/// Finds the provider DLL for this host's architecture: an explicit override, a published layout
	/// beside the host (<c>xaml-provider/&lt;rid&gt;</c>), or the repo build output. Null when none is
	/// present, so the caller can report it rather than fault.
	/// </summary>
	private static string? ResolveProviderPath()
	{
		var configured = Environment.GetEnvironmentVariable("ROSEMCP_XAML_PROVIDER");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

		var rid = RuntimeInformation.RuntimeIdentifier;
		var alongside = Path.Combine(AppContext.BaseDirectory, "xaml-provider", rid, ProviderFileName);
		if (File.Exists(alongside)) return alongside;

		var repositoryRoot = FindRepositoryRoot();
		if (repositoryRoot is null) return null;

		var providerBin = Path.Combine(repositoryRoot, "src", "RoseMcp.Xaml.Uwp.Tap", "bin", ProviderPlatform());
		if (!Directory.Exists(providerBin)) return null;

		return Directory.EnumerateFiles(providerBin, ProviderFileName, SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	/// <summary>The provider build platform matching this host's architecture (x64 or arm64).</summary>
	private static string ProviderPlatform() => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		_ => "x64",
	};

	private static string? FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName;
	}

	[DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
	private static extern int InitializeXamlDiagnosticsEx(
		string endPointName,
		uint pid,
		string? wszDllXamlDiagnostics,
		string wszTapDllName,
		Guid tapClsid,
		string wszInitializationData);
}
