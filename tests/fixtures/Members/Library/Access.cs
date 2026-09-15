namespace Library;

/// <summary>What a caller may do, one bit each.</summary>
[System.Flags]
public enum Access
{
	/// <summary>Nothing at all.</summary>
	None = 0,
	/// <summary>May read.</summary>
	Read = 1,
	/// <summary>May write.</summary>
	Write = 2
}
