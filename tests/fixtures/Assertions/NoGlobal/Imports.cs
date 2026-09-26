using Xunit;

namespace NoGlobal;

public static class Imports
{
	public static void Cases(int count)
	{
		Assert.Equal(1, count);
	}
}
