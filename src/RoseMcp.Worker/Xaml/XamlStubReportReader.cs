using System.Text.Json;

using Microsoft.CodeAnalysis;

using RoseMcp.XamlStubs;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Reads the stub generator's account of a project back out of the document it emits.
/// <para>
/// The generator cannot hand us an object. It lives in an analyzer assembly that Roslyn loads and
/// instantiates itself, so generated source is the only channel out, and it writes the account to
/// one document as JSON on a marked line. Only the marker and the hint name cross the boundary,
/// and both are constants, so nothing here depends on that assembly at runtime.
/// </para>
/// </summary>
public static class XamlStubReportReader
{
	/// <summary>Runs the project's generators if they have not run, then reads the report.</summary>
	public static async Task<XamlStubReport?> ReadAsync(Project project, CancellationToken cancellationToken)
	{
		var generated = await project.GetSourceGeneratedDocumentsAsync(cancellationToken);
		return await ReadAsync([.. generated], cancellationToken);
	}

	/// <summary>
	/// For a caller that already has the generated documents, since running generators is the slow
	/// part and doing it twice for one status call is the whole cost of the call again.
	/// <para>
	/// Anything unparseable reads as no report rather than as an error: a status call has to answer,
	/// and the honest answer to a malformed report is that there is not one.
	/// </para>
	/// </summary>
	public static async Task<XamlStubReport?> ReadAsync(
		IReadOnlyList<SourceGeneratedDocument> generated,
		CancellationToken cancellationToken)
	{
		var document = generated.FirstOrDefault(
			candidate => string.Equals(candidate.HintName, XamlStubReportChannel.HintName, StringComparison.Ordinal));

		if (document is null) return null;

		var text = (await document.GetTextAsync(cancellationToken)).ToString();

		var start = text.IndexOf(XamlStubReportChannel.Marker, StringComparison.Ordinal);
		if (start < 0) return null;

		var json = text[(start + XamlStubReportChannel.Marker.Length)..];
		var end = json.IndexOfAny(['\r', '\n']);
		if (end >= 0) json = json[..end];

		try
		{
			return JsonSerializer.Deserialize<XamlStubReport>(json);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
