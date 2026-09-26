namespace Tests;

public static class Collections
{
	public static void Cases(List<int> items)
	{
		Assert.Contains(3, items);
		Assert.Contains(items, item => item > 2);
		var only = Assert.Single(items);
		Assert.All(items, item => Assert.True(item > 0));
	}
}
