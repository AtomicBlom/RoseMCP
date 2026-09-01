using RoseMcp.Worker;

namespace RoseMcp.TestSupport;

/// <summary>Remembers everything an operation said about its own progress.</summary>
public sealed class CapturingProgress : IWorkProgress
{
	private readonly List<(string Message, double? Percent)> _reports = [];

	public IReadOnlyList<(string Message, double? Percent)> Reports
	{
		get
		{
			lock (_reports)
			{
				return [.. _reports];
			}
		}
	}

	public void Report(string message, double? percentComplete = null)
	{
		lock (_reports)
		{
			_reports.Add((message, percentComplete));
		}
	}
}
