using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Injects the RoseXamlTap diagnostics provider into the target and reads back what it reports (#2/#3).
/// <c>InitializeXamlDiagnosticsEx</c> (exported from Windows.UI.Xaml.dll) loads the provider into the
/// app by pid; the two ends exchange tab-separated files through a working folder this side stages and
/// grants the app's AppContainer rights to, since the provider runs sandboxed and cannot read Program
/// Files or write arbitrary paths. The provider must match the target's architecture, which is this
/// host's architecture -- an x64 provider for a classic UWP app emulated on ARM64.
/// </summary>
internal sealed class XamlDiagnosticsSession(ILogger logger)
{
	// Must match CLSID_RoseXamlTap in RoseXamlTap.cpp.
	private static readonly Guid ProviderClsid = new("7b9e5c10-2d4a-4f3b-9e21-a1b2c3d4e5f6");

	// The well-known diagnostics endpoint; anything else makes InitializeXamlDiagnosticsEx return
	// ERROR_NOT_FOUND (0x80070490).
	private const string EndpointName = "VisualDiagConnection1";

	private const string ProviderFileName = "RoseXamlTap.dll";
	private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Reads a snapshot of the target's live visual tree, injecting the provider first. Returns a tree
	/// with a <see cref="LiveXamlTree.Detail"/> and no nodes -- never throws -- when the provider is not
	/// available for this architecture, injection fails, or the target has no XAML UI.
	/// </summary>
	public LiveXamlTree ReadTree(int pid)
	{
		var provider = ResolveProviderPath();
		if (provider is null)
		{
			return new LiveXamlTree { Detail = $"The XAML provider ({ProviderFileName}) was not found for this host's architecture; build src/RoseXamlTap for {ProviderPlatform()}." };
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
			return new LiveXamlTree { Detail = $"Could not stage the XAML provider: {exception.Message}" };
		}

		var treeFile = Path.Combine(workDir, "tree.tsv");
		var readyFile = Path.Combine(workDir, "tree.ready");
		TryDelete(treeFile);
		TryDelete(readyFile);

		var hr = InitializeXamlDiagnosticsEx(EndpointName, (uint)pid, null, stagedProvider, ProviderClsid, workDir);
		if (hr < 0)
		{
			return new LiveXamlTree { Detail = $"InitializeXamlDiagnosticsEx failed (0x{hr:x8}); the target may have no XAML UI or not be a packaged app." };
		}

		if (!WaitForFile(readyFile, SnapshotTimeout))
		{
			return new LiveXamlTree { Detail = "The XAML provider was injected but did not produce a tree snapshot in time." };
		}

		try
		{
			var nodes = ParseTree(treeFile);
			logger.LogInformation("Read a XAML tree of {Count} element(s) from pid {Pid}.", nodes.Count, pid);
			return new LiveXamlTree { Nodes = nodes };
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Reading the XAML tree snapshot failed.");
			return new LiveXamlTree { Detail = $"Could not read the tree snapshot: {exception.Message}" };
		}
	}

	private (string WorkDir, string StagedProvider) StageSandboxFolder(string provider)
	{
		var workDir = Path.Combine(Path.GetTempPath(), "RoseMcpXaml", Environment.ProcessId.ToString());
		Directory.CreateDirectory(workDir);

		var stagedProvider = Path.Combine(workDir, ProviderFileName);
		File.Copy(provider, stagedProvider, overwrite: true);

		// ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2):
		// Modify grants read+execute to load the DLL and read commands, and write for the provider's
		// snapshot and log. Without this the sandboxed provider cannot touch the folder at all.
		foreach (var sid in new[] { "*S-1-15-2-1", "*S-1-15-2-2" })
		{
			Icacls(workDir, $"/grant {sid}:(OI)(CI)(M)");
		}

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

		var providerBin = Path.Combine(repositoryRoot, "src", "RoseXamlTap", "bin", ProviderPlatform());
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
