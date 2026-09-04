// A file header, which has to stay at the top.
using System.Globalization;

using Library.Nested;

namespace Library;

/// <summary>A file with opinions about its imports.</summary>
public static class Imports
{
	public static string Formatted(double value) => value.ToString(CultureInfo.InvariantCulture);

	public static string Deep() => Marker.Name;
}
