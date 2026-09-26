namespace Tests;

public static class Skips
{
	public static void Cases(int count)
	{
		// Shouldly's ShouldBe takes its receiver's type, so an int compared with a long does not bind.
		Assert.Equal(1L, count);
		Assert.Equal(2, count);
	}
}
