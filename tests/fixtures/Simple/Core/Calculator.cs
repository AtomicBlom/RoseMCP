namespace Core;

public static class Calculator
{
	public static int Add(int left, int right) => left + right;

	public static int Multiply(int left, int right) => left * right;

	private static int Twice(int value) => value * 2;
}
