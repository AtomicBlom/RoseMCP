namespace RoseMcp.Worker;

/// <summary>What the last sweep saw for a file, cheap enough to compare on every read.</summary>
public readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
	public static FileStamp? For(string path)
	{
		var info = new FileInfo(path);
		return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
	}
}
