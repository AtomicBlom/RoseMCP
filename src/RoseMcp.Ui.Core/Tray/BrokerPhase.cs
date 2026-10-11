namespace RoseMcp.Ui.Core.Tray;

/// <summary>Where the broker the tray hosts is in its life.</summary>
public enum BrokerPhase
{
	/// <summary>Being built and bound; it takes no calls yet.</summary>
	Starting,

	/// <summary>Taking calls.</summary>
	Serving,

	/// <summary>Stopping on the way out; it takes no more calls.</summary>
	Stopping,

	/// <summary>Did not start, and will not without the tray being started again.</summary>
	Failed,
}
