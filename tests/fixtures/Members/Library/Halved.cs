namespace Library;

/// <summary>One type in two blocks of the same file, which is still one type.</summary>
public partial class Halved
{
	public int FrontHalf => 1;
}

public partial class Halved
{
	public int BackHalf => 2;
}
