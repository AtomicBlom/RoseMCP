using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RoseMcp.Symbols;

/// <summary>
/// A module's portable PDB, answering the two questions a debugger cannot answer from metadata: what
/// a local is called, and which line an IL offset came from.
/// <para>
/// Both are what separates a readable stopped frame from a numbered one. Without this, locals are
/// <c>local_0</c> upwards by slot and a frame is a method name with no line, so a reader looking at
/// a stack has to find the code by hand and a caller told "local_3" has to count declarations to
/// guess which variable that is.
/// </para>
/// </summary>
public sealed class PortablePdb : IDisposable
{
	private readonly MetadataReaderProvider _provider;
	private readonly MetadataReader _pdb;

	private PortablePdb(MetadataReaderProvider provider, MetadataReader pdb, string? path)
	{
		_provider = provider;
		_pdb = pdb;
		Path = path;
	}

	/// <summary>Where the symbols came from, or null when they were embedded in the module itself.</summary>
	public string? Path { get; }

	/// <summary>
	/// Opens a reader over a provider somebody else established. Ownership of the provider passes
	/// here, so disposing this disposes it.
	/// </summary>
	internal static PortablePdb Over(MetadataReaderProvider provider, string? path) =>
		new(provider, provider.GetMetadataReader(), path);

	/// <summary>
	/// The names of the locals in scope at an IL offset, by slot.
	/// <para>
	/// Only the scopes covering that offset, innermost last, so a slot reused by two locals in
	/// sibling blocks gets the name belonging to the block being executed. Compiler-generated locals
	/// have no name in the PDB at all and are simply absent, which is the honest answer: a caller
	/// asking about a slot the compiler invented gets nothing rather than somebody else's name.
	/// </para>
	/// </summary>
	public IReadOnlyDictionary<int, string> LocalNames(int methodToken, int ilOffset)
	{
		var named = new Dictionary<int, string>();

		foreach (var scope in LocalScopes(methodToken))
		{
			if (!scope.Covers(ilOffset)) continue;

			foreach (var local in scope.Locals)
			{
				// Later scopes win, and the enumeration is outermost first, so an inner block's name
				// for a reused slot replaces the outer one.
				named[local.Slot] = local.Name;
			}
		}

		return named;
	}

	/// <summary>
	/// Every lexical scope in a method, outermost first, with the locals declared in each.
	/// <para>
	/// Exposed as well as used, because the nesting is the part that is easy to get wrong and a test
	/// asserting a name at an offset cannot show whether the scopes were read correctly or the offset
	/// happened to fall somewhere forgiving.
	/// </para>
	/// </summary>
	public IReadOnlyList<LocalScopeInfo> LocalScopes(int methodToken)
	{
		try
		{
			var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
			var scopes = new List<LocalScopeInfo>();

			foreach (var scopeHandle in _pdb.GetLocalScopes(handle))
			{
				var scope = _pdb.GetLocalScope(scopeHandle);
				var locals = new List<LocalName>();

				foreach (var localHandle in scope.GetLocalVariables())
				{
					var local = _pdb.GetLocalVariable(localHandle);

					// DebuggerHidden locals are the compiler's own and are not the caller's business.
					if ((local.Attributes & LocalVariableAttributes.DebuggerHidden) != 0) continue;

					locals.Add(new LocalName { Slot = local.Index, Name = _pdb.GetString(local.Name) });
				}

				scopes.Add(new LocalScopeInfo
				{
					StartOffset = scope.StartOffset,
					Length = scope.Length,
					Locals = locals,
				});
			}

			return scopes;
		}
		catch (Exception)
		{
			// A method with no debug information, or a token this PDB does not describe. Absent
			// symbols are an ordinary state and the caller falls back to slot numbers.
			return [];
		}
	}

	/// <summary>
	/// Every sequence point in a method, in IL order.
	/// <para>
	/// Documents are read per point rather than once, because a method can span files: a partial
	/// method's halves, or anything a source generator wove together.
	/// </para>
	/// </summary>
	public IReadOnlyList<SequencePointInfo> SequencePointsOf(int methodToken)
	{
		try
		{
			var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodToken);
			var debugInformation = _pdb.GetMethodDebugInformation(handle);
			var points = new List<SequencePointInfo>();

			foreach (var point in debugInformation.GetSequencePoints())
			{
				if (point.IsHidden)
				{
					points.Add(new SequencePointInfo { Offset = point.Offset, IsHidden = true });
					continue;
				}

				points.Add(new SequencePointInfo
				{
					Offset = point.Offset,
					IsHidden = false,
					Position = new SourcePosition
					{
						File = DocumentName(point.Document),
						Line = point.StartLine,
						Column = point.StartColumn,
						EndLine = point.EndLine,
						EndColumn = point.EndColumn,
					},
				});
			}

			return points;
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary>Where an IL offset in a method came from, or null when it maps to no source.</summary>
	public SourcePosition? Position(int methodToken, int ilOffset) =>
		SequencePoints.Nearest(SequencePointsOf(methodToken), ilOffset);

	private string DocumentName(DocumentHandle handle)
	{
		if (handle.IsNil) return string.Empty;

		return _pdb.GetString(_pdb.GetDocument(handle).Name);
	}

	public void Dispose() => _provider.Dispose();
}
