using System.Text.Encodings.Web;
using System.Text.Json;

namespace RoseMcp.Ui.Core.Views;

/// <summary>An agent's arguments and Rose's answers as a person reads them: indented, with nothing escaped that need not be.</summary>
public static class JsonText
{
	private static readonly JsonSerializerOptions Indented = new()
	{
		WriteIndented = true,

		// Source is the usual payload, and the default encoder writes every angle bracket, ampersand and quote
		// in it as a \u escape -- correct JSON, and code nobody can read. This is text for a window, never markup.
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>
	/// <paramref name="text"/> indented where it is JSON, and as it is where it is not: a response kept as text, or
	/// one cut for its length partway through an object, is still worth reading as written.
	/// </summary>
	public static string Pretty(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return string.Empty;

		try
		{
			using var document = JsonDocument.Parse(text);

			return JsonSerializer.Serialize(document.RootElement, Indented);
		}
		catch (JsonException)
		{
			return text;
		}
	}
}
