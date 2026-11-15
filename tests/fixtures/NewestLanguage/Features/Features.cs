namespace Features;

/// <summary>C# 15 and nothing older: each member reports CS9327 under C# 14.</summary>
public static class Newest
{
	/// <summary>Collection expression arguments.</summary>
	public static List<int> WithCapacity() => [with(capacity: 32), 1, 2, 3];

	/// <summary>Labeled break and continue.</summary>
	public static int FirstAnswer(int[][] rows)
	{
		var found = 0;
		outer: foreach (var row in rows)
		{
			foreach (var value in row)
			{
				if (value < 0) continue outer;
				if (value == 42)
				{
					found = value;
					break outer;
				}
			}
		}

		return found;
	}
}
