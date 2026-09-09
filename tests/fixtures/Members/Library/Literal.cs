namespace Library;

/// <summary>
/// A multi-line raw string literal already on disk, indented more deeply than any write path
/// would place it, so every write to this file moves it and none of them can pass by leaving it
/// alone.
/// <para>
/// Its value is what is left once the closing delimiter's indentation comes off every line, so
/// anything that moves the content without moving the delimiter changes what the program says --
/// and a blank line inside it is content that no formatter and no analyzer has an opinion about.
/// </para>
/// </summary>
public static class Literal
{
	public static string Report()
	{
		return """
						first

						second
						""";
	}
}
