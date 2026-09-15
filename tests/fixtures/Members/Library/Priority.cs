namespace Library;

/// <summary>How soon, in one byte.</summary>
public enum Priority : byte
{
	Low = 0x01,
	High = 1 << 4,
	Highest = byte.MaxValue,
}
