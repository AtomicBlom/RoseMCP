namespace App;

/// <summary>
/// In the project that references Core, so Core cannot see it however it is imported. That is a
/// different fix from a missing using, and reporting it as one would send the caller the wrong way.
/// </summary>
public static class Announcer
{
	public static string Announce() => "announced";
}
