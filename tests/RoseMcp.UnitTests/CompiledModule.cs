using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace RoseMcp.UnitTests;

/// <summary>
/// A small assembly and its portable PDB, compiled into a temporary directory so a symbol reader has
/// something real to read.
/// <para>
/// Compiled here rather than read out of the test assembly's own symbols, because what those contain
/// depends on how the suite was built. Optimised code loses a local to a stack temp and merges the
/// scope it was declared in, so the nesting and the names these tests are about would be present in
/// a Debug run and absent in the Release one CI also does -- a test that passes for a reason nobody
/// chose. The optimisation level is stated here instead, and the source is in the test beside the
/// assertions about its lines.
/// </para>
/// </summary>
internal sealed class CompiledModule : IDisposable
{
	/// <summary>
	/// A method with a local in its body and another in a nested block, which is the smallest shape
	/// that shows a slot's name depending on where execution is.
	/// </summary>
	internal const string NestedLocalsSource = """
		namespace Probe;

		public static class Frames
		{
			public static int Nested(int seed)
			{
				var outerTotal = seed + 1;

				if (seed > 0)
				{
					var innerStep = outerTotal * 2;
					outerTotal += innerStep;
				}

				return outerTotal;
			}
		}
		""";

	private readonly string _directory;
	private readonly string[] _lines;

	private CompiledModule(string directory, string source, string modulePath, string pdbPath, string sourcePath)
	{
		_directory = directory;
		_lines = source.Split('\n');
		ModulePath = modulePath;
		PdbPath = pdbPath;
		SourcePath = sourcePath;
	}

	/// <summary>The assembly on disk.</summary>
	internal string ModulePath { get; }

	/// <summary>Where the symbols were written, whether or not any were.</summary>
	internal string PdbPath { get; }

	/// <summary>The source file, which is the path the PDB records for every sequence point.</summary>
	internal string SourcePath { get; }

	/// <summary><see cref="NestedLocalsSource"/>, compiled.</summary>
	internal static CompiledModule NestedLocals(bool withSymbols = true) =>
		Of(NestedLocalsSource, "Probe", withSymbols);

	/// <summary>Compiles one file into a directory of its own.</summary>
	/// <param name="source">The C#, which is also written to disk so the recorded path names a real file.</param>
	/// <param name="name">The assembly name, and the stem of every file written.</param>
	/// <param name="withSymbols">
	/// False emits no PDB and no debug directory entry, which is how a module that names no symbols
	/// at all is arranged.
	/// </param>
	internal static CompiledModule Of(string source, string name = "Probe", bool withSymbols = true)
	{
		var directory = Path.Combine(Path.GetTempPath(), "rose-symbols", Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(directory);

		var sourcePath = Path.Combine(directory, $"{name}.cs");
		var modulePath = Path.Combine(directory, $"{name}.dll");
		var pdbPath = Path.Combine(directory, $"{name}.pdb");

		File.WriteAllText(sourcePath, source, Encoding.UTF8);

		// The encoding is required rather than decoration: emitting debug information for a tree
		// without one is CS8055, since the source checksum cannot be computed.
		var tree = CSharpSyntaxTree.ParseText(source, path: sourcePath, encoding: Encoding.UTF8);

		var compilation = CSharpCompilation.Create(
			name,
			[tree],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

		using var module = new MemoryStream();
		using var symbols = new MemoryStream();

		var emitted = withSymbols
			? compilation.Emit(
				module,
				symbols,
				options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath))
			: compilation.Emit(module);

		if (!emitted.Success)
		{
			throw new InvalidOperationException(
				$"The fixture did not compile: {string.Join("; ", emitted.Diagnostics.Select(diagnostic => diagnostic.ToString()))}");
		}

		File.WriteAllBytes(modulePath, module.ToArray());
		if (withSymbols) File.WriteAllBytes(pdbPath, symbols.ToArray());

		return new CompiledModule(directory, source, modulePath, pdbPath, sourcePath);
	}

	/// <summary>
	/// The 1-based line a piece of the source is on, so a test names a line by the code on it rather
	/// than by a number that moves when the fixture is edited.
	/// </summary>
	internal int LineOf(string text)
	{
		for (var index = 0; index < _lines.Length; index++)
		{
			if (_lines[index].Contains(text, StringComparison.Ordinal)) return index + 1;
		}

		throw new InvalidOperationException($"The fixture source has no line containing '{text}'.");
	}

	/// <summary>
	/// A method's metadata token, which is how a debugger addresses one and therefore what a symbol
	/// reader is asked about.
	/// </summary>
	internal static int TokenOf(MetadataReader metadata, string methodName)
	{
		foreach (var handle in metadata.MethodDefinitions)
		{
			if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) != methodName) continue;

			return MetadataTokens.GetToken(handle);
		}

		throw new InvalidOperationException($"The module declares no method named '{methodName}'.");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// A temp directory left behind is litter rather than a failure, and failing a test over
			// one would report a passing reader as broken.
		}
	}
}
