namespace Shared;

/// <summary>
/// Compiled by both solutions in this fixture, which is the whole point of it: a change written
/// here through one of them is on disk for the other, whose own projects still call the old name.
/// </summary>
public sealed class Widget
{
	public string Describe() => "widget";
}