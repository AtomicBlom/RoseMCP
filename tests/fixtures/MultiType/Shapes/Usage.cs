namespace Shapes;

/// <summary>Uses the others, so a move that broke a binding shows up as a diagnostic.</summary>
public static class Usage
{
	public static double Total() => new Circle(2).Area() + new Square(3).Area();

	public static string Describe(ShapeKind kind) => kind switch
	{
		ShapeKind.Circle => new Circle(1).Describe(),
		_ => "square",
	};
}
