using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Contracts;

/// <summary>
/// How these types go over the wire outside MCP -- which in practice means GET /admin/workspaces,
/// in both hosts.
/// <para>
/// Defined once because the point of that endpoint is to return exactly what the tray window
/// renders. Two hosts each reaching for the framework default is two chances for one of them to
/// serialise an enum as a number, which is what happened: an outcome reading "1" tells a reader
/// nothing at all.
/// </para>
/// </summary>
public static class ContractJson
{
	/// <summary>Web defaults for the casing, string enums so the values are readable.</summary>
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};
}
