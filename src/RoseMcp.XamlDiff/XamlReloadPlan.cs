namespace RoseMcp.XamlDiff;

/// <summary>
/// What to diff a file's current contents against, from <see cref="XamlReloadBaseline.Prepare"/>.
/// <see cref="OldXaml"/> being null means there is nothing to diff against and <see cref="Note"/>
/// says why; a note beside a baseline is worth passing on too, since "nothing changed" and "the diff
/// found nothing" both come out as zero edits.
/// </summary>
public sealed record XamlReloadPlan
{
	public string? OldXaml { get; init; }

	public string? Note { get; init; }
}
