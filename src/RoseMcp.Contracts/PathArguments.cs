using System.Text.Json;

namespace RoseMcp.Contracts;

/// <summary>
/// Which arguments on this surface are always a file path, and whether a call sent one that is not
/// absolute.
/// <para>
/// Only the broker knows what a relative path is measured from: the calling session says where it
/// is standing, and no process further in is told. A worker resolves one against its own working
/// directory, which is its solution's root -- so a path that was meant for another checkout of the
/// same repository names a real file there, is written to, and is reported as a success. Making the
/// hop absolute-only is what turns that into a refusal: the same mis-route now fails at the first
/// thing that looks at the argument, naming it.
/// </para>
/// <para>
/// Both ends read this one list, so they cannot disagree about which arguments it covers -- the
/// broker makes exactly these absolute before forwarding, and the hosts refuse exactly these when
/// they are not. Here rather than in either of them because there are three MCP boundaries and the
/// hosts cannot reference each other, and it stays inside what this assembly is for: a pure
/// function over strings and JSON with no dependency on the MCP packages.
/// </para>
/// <para>
/// An argument with its own base does not belong here. <c>targetPath</c> on
/// <c>rose_move_type_to_file</c> is measured from the file being split, which is a fact the worker
/// has and the broker does not, so it travels as the caller wrote it.
/// </para>
/// </summary>
public static class PathArguments
{
	/// <summary>
	/// The argument names that are always a path to a file, however the tool spells the rest of its
	/// arguments. A name here is one the broker resolves and the hosts require absolute.
	/// </summary>
	public static readonly IReadOnlyList<string> Names = ["filePath", "filePaths"];

	/// <summary>Whether an argument name is one of them.</summary>
	public static bool NamesAPath(string argument) => Names.Contains(argument, StringComparer.Ordinal);

	/// <summary>
	/// A sentence naming the first path argument that arrived relative, or null when every one of
	/// them is absolute. A missing or null argument is not a relative path, and neither is one whose
	/// value is not a string: the binder's own refusal says more about those than this could.
	/// </summary>
	public static string? Relative(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
	{
		if (arguments is null) return null;

		foreach (var (name, value) in arguments)
		{
			if (!NamesAPath(name)) continue;

			// One name covers both shapes: rose_format takes a list where every other tool takes one.
			IEnumerable<JsonElement> supplied =
				value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];

			foreach (var element in supplied)
			{
				if (element.ValueKind != JsonValueKind.String) continue;
				if (element.GetString() is not { Length: > 0 } path) continue;
				if (Path.IsPathFullyQualified(path)) continue;

				return $"{name} has to be an absolute path, and '{path}' is not one. What a relative path "
					+ "is measured from is known only to the broker, which makes one absolute before "
					+ "forwarding it -- so a relative path arriving here did not come through there.";
			}
		}

		return null;
	}
}
