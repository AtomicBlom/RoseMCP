using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// What an edit was asked to change, as spans of each file as it stood before the edit.
/// <para>
/// Declared by the tool that makes the edit, because nothing else can know it. A caller naming a
/// member asked for that member and not for the one below it; a caller anchoring on three tokens
/// asked for those three and not for the statement around them. <see cref="Overreach"/> holds the
/// write to it, and a file the edit changed without naming here is a file it changed on its own.
/// </para>
/// </summary>
internal sealed class Asked
{
	private readonly ImmutableDictionary<string, ImmutableArray<TextSpan>> _spans;

	private Asked(ImmutableDictionary<string, ImmutableArray<TextSpan>> spans)
	{
		_spans = spans;
	}

	/// <summary>Nothing in any file already there, which is what creating a file asks for.</summary>
	internal static Asked Nothing { get; } =
		new(ImmutableDictionary.Create<string, ImmutableArray<TextSpan>>(StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// These spans of <paramref name="document"/> as well as whatever was asked already. A document
	/// with no path is not a file anything writes, so it asks for nothing.
	/// </summary>
	internal Asked And(Document document, IEnumerable<TextSpan> spans)
	{
		if (document.FilePath is not { Length: > 0 } path) return this;

		var had = _spans.TryGetValue(path, out var existing) ? existing : [];

		return new Asked(_spans.SetItem(path, had.AddRange(spans)));
	}

	/// <summary>This span of <paramref name="document"/> as well as whatever was asked already.</summary>
	internal Asked And(Document document, TextSpan span) => And(document, [span]);

	/// <summary>What was asked of the file at <paramref name="path"/>, which is nothing for a file never named.</summary>
	internal IReadOnlyList<TextSpan> Of(string path) => _spans.TryGetValue(path, out var spans) ? spans : [];
}
