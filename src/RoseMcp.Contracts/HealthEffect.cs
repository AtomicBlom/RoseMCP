namespace RoseMcp.Contracts;

/// <summary>
/// What a reason in a workspace's health does to the answers it gives, which is what decides how loudly
/// it is shown.
/// <para>
/// Three values rather than a severity scale, because each says what a reader should do. A reason that
/// blocks answers is one nothing the workspace says can be trusted past; one that may affect some
/// projects leaves every answer about the others as good as a healthy workspace's; a note is worth
/// knowing and changes no answer, which is why a note never makes a workspace degraded.
/// </para>
/// </summary>
public enum HealthEffect
{
	/// <summary>No answer from the workspace can be trusted until this is fixed: a failed restore, the wrong platform, a stopped worker.</summary>
	BlocksAnswers,

	/// <summary>Answers about the projects it names can be incomplete; answers about the rest are not affected.</summary>
	MayAffectSomeProjects,

	/// <summary>Worth knowing and changes no answer: a configuration chosen, a stale build output, a legacy project evaluated elsewhere.</summary>
	Note,
}
