namespace RoseMcp.Contracts;

/// <summary>What became of one reason given to <c>rose_workspace_acknowledge</c>.</summary>
public enum AcknowledgementOutcome
{
	/// <summary>The calling session's reads stop saying it, until the session ends or a new reason appears.</summary>
	Acknowledged,

	/// <summary>A proposal that the person accept it is waiting for the person.</summary>
	Proposed,

	/// <summary>The person has already accepted it, so there is nothing to propose.</summary>
	AlreadyAccepted,

	/// <summary>No reason in the workspace's current health has this id.</summary>
	NotFound,
}
