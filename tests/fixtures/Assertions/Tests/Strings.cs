namespace Tests;

public static class Strings
{
	public static void Cases(string text)
	{
		Assert.Contains("a", text, StringComparison.Ordinal);
		Assert.Contains("b", text, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("c", text);
		Assert.DoesNotContain('\r', text);
	}
}
