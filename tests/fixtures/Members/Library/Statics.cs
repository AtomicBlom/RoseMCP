using System.Globalization;

using static System.Math;

namespace Library;

/// <summary>
/// A file whose imports end in a static one. A static using sorts by its own rules and this
/// repository puts every one of them last, so a plain using added here belongs above it -- which
/// compiles either way and trips no analyzer, and is therefore only ever caught by looking.
/// </summary>
public static class Statics
{
	public static double Rounded(double value) => Round(value, 2);

	public static string Formatted(double value) => value.ToString(CultureInfo.InvariantCulture);
}
