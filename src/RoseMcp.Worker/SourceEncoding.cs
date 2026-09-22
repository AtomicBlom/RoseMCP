using System.Text;

using Microsoft.CodeAnalysis.Text;

namespace RoseMcp.Worker;

/// <summary>
/// Which encoding a source file is read and written with.
/// <para>
/// A byte order mark is part of the file, not a detail of how it was read, so whatever rewrites a
/// file puts back the mark it found and adds none to a file that had none. Both halves are silent
/// when they go wrong: the mark is three bytes no diff shows, and the file still compiles either
/// way, so the first thing anyone sees is a review full of whole-file changes or a tool downstream
/// that keys on the mark and stops finding it.
/// </para>
/// </summary>
public static class SourceEncoding
{
	/// <summary>
	/// UTF-8 that emits no byte order mark, which is what a file without one has to be read as.
	/// <para>
	/// <see cref="Encoding.UTF8"/> is UTF-8 <em>with</em> a preamble, so handing it to
	/// <see cref="SourceText.From(Stream, Encoding, SourceHashAlgorithm, bool, bool)"/> as the
	/// fallback gives every mark-less file a <see cref="SourceText.Encoding"/> that emits one, and
	/// the next write puts a mark on a file nobody asked to change that way. Detection still wins
	/// where a mark is present, so this decides only the case it is named for.
	/// </para>
	/// </summary>
	public static readonly Encoding Utf8WithoutMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Writes <paramref name="text"/> to <paramref name="path"/> in its own encoding, creating the
	/// file if it is absent and truncating it if it is not.
	/// </summary>
	/// <remarks>
	/// <see cref="File.WriteAllTextAsync(string, string, CancellationToken)"/> is UTF-8 without a mark
	/// whatever the file already was, so routing a rewrite through it strips the mark off every file
	/// that had one. Text with no encoding of its own is text nothing read off disk -- a file this
	/// call is creating -- and it gets <see cref="Utf8WithoutMark"/> rather than a mark it was never
	/// given.
	/// </remarks>
	public static async Task WriteAsync(string path, SourceText text, CancellationToken cancellationToken)
	{
		// FileShare.Read is what File.WriteAllText grants, and taking it away would fail the write
		// whenever an editor or the disk barrier happens to have the file open for reading.
		await using var stream = new FileStream(
			path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);

		// StreamWriter emits the preamble on its first write and only from offset zero, which is
		// what FileMode.Create guarantees.
		await using var writer = new StreamWriter(stream, text.Encoding ?? Utf8WithoutMark);

		text.Write(writer, cancellationToken);
	}
}
