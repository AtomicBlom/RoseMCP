namespace Library;

/// <summary>Members inside a region, so removing one has to leave the pair balanced.</summary>
public sealed class Regioned
{
	#region Helpers

	/// <summary>Doubles it.</summary>
	public static int Twice(int value) => value * 2;

	/// <summary>Trebles it.</summary>
	public static int Thrice(int value) => value * 3;

	#endregion
}
