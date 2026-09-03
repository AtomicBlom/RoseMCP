namespace Library;

/// <summary>Anything with an area.</summary>
public interface IShape
{
	double Area();
}

/// <summary>Which colour to paint it.</summary>
public enum Colour
{
	Red,

	Green,
}

/// <summary>Nothing in it yet, which is its own case to get right.</summary>
public sealed class Empty
{
}
