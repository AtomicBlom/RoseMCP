namespace Tests;

public static class Equality
{
	public static void Cases(int count, string text, double ratio)
	{
		Assert.Equal(1, count);
		Assert.Equal(actual: count, expected: 2);
		Assert.Equal("a", text, ignoreCase: true);
		Assert.Equal(1.5, ratio, 3);
		Assert.True(count > 0);
		Assert.True(!(count > 1 && count < 5));
	}
}
