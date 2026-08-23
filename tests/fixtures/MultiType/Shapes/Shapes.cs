using System.Globalization;

namespace Shapes;

/// <summary>Anything with an area.</summary>
public interface IShape
{
	double Area();
}

/// <summary>Which shape a description refers to.</summary>
public enum ShapeKind
{
	Circle,

	Square,
}

/// <summary>
/// A circle.
/// <para>
/// The only type here that needs invariant formatting, so the using above belongs with it once
/// these are separated.
/// </para>
/// </summary>
public sealed record Circle(double Radius) : IShape
{
	public double Area() => Math.PI * Radius * Radius;

	public string Describe() => Radius.ToString("F2", CultureInfo.InvariantCulture);
}

/// <summary>A square, kept last so moving something out of the middle is what gets exercised.</summary>
public sealed class Square(double side) : IShape
{
	public double Area() => side * side;
}
