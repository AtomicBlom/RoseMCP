namespace App;

/// <summary>One real error, which a solution-wide pass has to keep showing.</summary>
public static class Mistake
{
	public static int Wrong() => "not a number";
}