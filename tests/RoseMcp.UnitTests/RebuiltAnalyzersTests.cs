using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which analyzer assemblies read as rebuilt since this process loaded them. Compared with the stamp the
/// loader copied, so what the tests vary is the file on disk against a stamp they choose, which is exactly
/// what a rebuild changes.
/// </summary>
public sealed class RebuiltAnalyzersTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "rose-rebuilt", Guid.NewGuid().ToString("n"));
	private readonly Dictionary<string, FileStamp> _copied = new(StringComparer.OrdinalIgnoreCase);
	private int _stampLookups;

	public RebuiltAnalyzersTests() => Directory.CreateDirectory(_root);

	[Test]
	public void An_assembly_unchanged_since_it_was_copied_is_not_rebuilt()
	{
		var generator = Loaded("Gen.dll");
		var rebuilt = Tracking(generator);

		rebuilt.Check().ShouldBeEmpty();
		RebuiltAnalyzers.Notice(rebuilt.Current).ShouldBeNull();
	}

	/// <summary>
	/// The case the whole class is for: the file on disk is not the build loaded from it, and every read
	/// says which assembly and what picks the new build up.
	/// </summary>
	[Test]
	public void A_rebuilt_assembly_is_named_with_what_picks_it_up()
	{
		var generator = Loaded("Gen.dll");
		var rebuilt = Tracking(generator);

		Rebuild(generator);

		rebuilt.Check().ShouldBe([generator]);
		rebuilt.Current.ShouldBe([generator]);

		var notice = RebuiltAnalyzers.Notice(rebuilt.Current).ShouldNotBeNull();
		notice.ShouldStartWith("Gen.dll was rebuilt");
		notice.ShouldContain("rose_workspace_reload", Case.Sensitive);
	}

	/// <summary>Nothing is loaded from an assembly not yet copied, so nothing about it can be stale.</summary>
	[Test]
	public void An_assembly_not_yet_copied_is_not_rebuilt()
	{
		var generator = Path.Combine(_root, "Gen.dll");
		File.WriteAllText(generator, "built");

		Tracking(generator).Check().ShouldBeEmpty();
	}

	/// <summary>
	/// A rebuild can delete the file before writing the new one. Calling that rebuilt would be a guess,
	/// and forgetting a rebuild already seen would tell the next read the old build is current.
	/// </summary>
	[Test]
	public void An_assembly_missing_mid_rebuild_keeps_the_verdict_it_had()
	{
		var rebuiltOne = Loaded("Rebuilt.dll");
		var currentOne = Loaded("Current.dll");
		var rebuilt = Tracking(rebuiltOne, currentOne);

		Rebuild(rebuiltOne);
		rebuilt.Check().ShouldBe([rebuiltOne]);

		File.Delete(rebuiltOne);
		File.Delete(currentOne);

		rebuilt.Check().ShouldBe([rebuiltOne]);
	}

	/// <summary>
	/// A reload in place takes analyzers from the same loader, which hands back the copy it already made,
	/// so the assembly is still the old build and still says so. One the reload no longer references is no
	/// longer anything the answers depend on.
	/// </summary>
	[Test]
	public void A_reload_in_place_keeps_a_rebuild_it_still_references_and_drops_one_it_does_not()
	{
		var kept = Loaded("Kept.dll");
		var dropped = Loaded("Dropped.dll");
		var rebuilt = Tracking(kept, dropped);

		Rebuild(kept);
		Rebuild(dropped);
		rebuilt.Check().Count.ShouldBe(2);

		rebuilt.Track([kept]);

		rebuilt.Current.ShouldBe([kept]);
		rebuilt.Check().ShouldBe([kept]);
	}

	/// <summary>
	/// Every project of a solution names the SDK's analyzers, so the check is once per path, not once per
	/// project naming it: the barrier pays for it on every read.
	/// </summary>
	[Test]
	public void A_path_shared_by_several_projects_is_checked_once()
	{
		var analyzer = Loaded("Shared.dll");
		var rebuilt = Tracking(analyzer, analyzer.ToUpperInvariant(), analyzer);

		rebuilt.Check();

		_stampLookups.ShouldBe(1);
	}

	/// <summary>
	/// The XAML stub generator is an analyzer reference too, but it ships with the worker and changes only
	/// with a new one, so it is never the assembly somebody has just rebuilt.
	/// </summary>
	[Test]
	public void The_workers_own_analyzers_are_left_out()
	{
		var own = Path.Combine(_root, "worker");
		Directory.CreateDirectory(own);

		var stubs = Loaded(Path.Combine("worker", "RoseMcp.XamlStubs.dll"));
		var generator = Loaded("Gen.dll");

		using var workspace = new AdhocWorkspace();
		var solution = workspace.CurrentSolution
			.AddProject("Consumer", "Consumer", LanguageNames.CSharp)
			.AddAnalyzerReference(new AnalyzerFileReference(stubs, new NoLoader()))
			.AddAnalyzerReference(new AnalyzerFileReference(generator, new NoLoader()))
			.Solution;

		var rebuilt = new RebuiltAnalyzers(CopiedStamp, own);
		rebuilt.Track(solution);

		Rebuild(stubs);
		Rebuild(generator);

		rebuilt.Check().ShouldBe([generator]);
	}

	[Test]
	public void Several_assemblies_are_named_once_each()
	{
		RebuiltAnalyzers.Notice([]).ShouldBeNull();

		RebuiltAnalyzers.Notice([Path.Combine(_root, "net8.0", "A.dll"), Path.Combine(_root, "netstandard2.0", "A.dll"), Path.Combine(_root, "B.dll")])
			.ShouldNotBeNull()
			.ShouldStartWith("A.dll and B.dll were rebuilt");

		RebuiltAnalyzers.Notice([.. "ABCDE".Select(name => Path.Combine(_root, $"{name}.dll"))])
			.ShouldNotBeNull()
			.ShouldStartWith("A.dll, B.dll and 3 more were rebuilt");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// A temp directory left behind is litter, not a failure.
		}
	}

	private RebuiltAnalyzers Tracking(params string[] paths)
	{
		var rebuilt = new RebuiltAnalyzers(CopiedStamp, Path.Combine(_root, "worker"));
		rebuilt.Track(paths);

		return rebuilt;
	}

	private FileStamp? CopiedStamp(string path)
	{
		_stampLookups++;
		return _copied.TryGetValue(path, out var stamp) ? stamp : null;
	}

	/// <summary>An assembly on disk, recorded as copied in the state it is in now.</summary>
	private string Loaded(string name)
	{
		var path = Path.Combine(_root, name);
		File.WriteAllText(path, "first build");
		_copied[path] = FileStamp.For(path)!.Value;

		return path;
	}

	/// <summary>What a rebuild does to the file: a new length, and a write time after the first.</summary>
	private static void Rebuild(string path)
	{
		File.WriteAllText(path, "the second build, longer than the first");
		File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
	}

	/// <summary>Constructing a reference registers it with its loader; nothing here loads one.</summary>
	private sealed class NoLoader : IAnalyzerAssemblyLoader
	{
		public void AddDependencyLocation(string fullPath)
		{
		}

		public Assembly LoadFromPath(string fullPath) => throw new InvalidOperationException("Nothing here loads an analyzer.");
	}
}
