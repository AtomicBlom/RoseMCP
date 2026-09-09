namespace RoseMcp.IntegrationTests;

/// <summary>
/// Which live-app tests may overlap, and in what order they are handed an app.
/// <para>
/// Declared as parallel constraints rather than taken as a lock inside the fixture, because the
/// runner is the only party that can see the whole queue. A lock serialises arrivals but cannot
/// choose which to admit first, so the tests that end the app were interleaved with the tests that
/// share one and the app was launched seven times where once would do -- and under a runner that
/// dispatches every test at once, two dozen of them sat blocked in that lock holding scheduler slots
/// that the rest of the suite could have used.
/// </para>
/// <para>
/// Constraint keys exclude symmetrically: two tests sharing any key never overlap, and two sharing
/// none always may. So a phase is expressed as a key set rather than as a role. A slot test holds one
/// key of the pool; a test wanting the whole probe holds every key in it. Slot tests therefore
/// overlap each other and nothing else, which is exactly what a scratch slot buys.
/// </para>
/// <para>
/// A key is as narrow as the thing it protects, and no narrower. Too wide costs wall clock: it was
/// measured that only one of two hundred and ten other tests overlapped a live-app test, so keying the
/// live-app half against the rest of the suite made its two halves add up (268s + 109s) rather than
/// overlap. Too narrow costs correctness, and costs it dishonestly -- see LiveApp below.
/// </para>
/// </summary>
internal static class ProbeKeys
{
	/// <summary>
	/// How many slot tests may overlap. It has to be at least the number of them, and above that the
	/// pool costs nothing. Two slot tests given the same index lose their overlap and nothing else,
	/// which is why the index is written at the test rather than derived: getting it wrong is slow
	/// rather than wrong, and a slot test that silently shared a scratch slot would be the opposite.
	/// </summary>
	public const int SlotKeys = 16;

	/// <summary>
	/// The order a phase is admitted in. Shared-app tests before app-ending ones, so a single launch
	/// serves every test that can use it: admit one app-ending test first and the next shared test
	/// pays a relaunch, which is where six of the seven launches came from.
	/// </summary>
	public const int SlotPhase = 10;

	/// <inheritdoc cref="SlotPhase"/>
	public const int SessionPhase = 20;

	/// <inheritdoc cref="SlotPhase"/>
	public const int OwnAppPhase = 30;

	/// <summary>The key one slot test holds, refusing an index the pool does not cover.</summary>
	public static string ClassicSlot(int slot) =>
		slot >= 0 && slot < SlotKeys
			? $"ClassicUwp:Slot{slot}"
			: throw new ArgumentOutOfRangeException(
				nameof(slot),
				slot,
				$"Slot keys run from 0 to {SlotKeys - 1}. Raise ProbeKeys.SlotKeys to add more.");

	/// <summary>Every slot key, which is what a test wanting the whole classic probe holds.</summary>
	public static readonly string[] AllClassicSlots = [.. Enumerable.Range(0, SlotKeys).Select(ClassicSlot)];

	/// <summary>
	/// Held by every live-app test, whichever probe it drives. Not because the probes contend over
	/// something shared -- they are separate packages and separate processes -- but because injecting
	/// the XAML tap sometimes wedges the app it is injected into, and load makes that far likelier.
	/// Letting the probes run concurrently took the suite from 1 failure to 14 and from 325s to 1067s.
	/// <para>
	/// The failures are one wedged app rather than fourteen races, which is why serialising helps so
	/// much: the app stops executing the instant an injection fails to return, every later test on that
	/// app fails the same way, and the fixture goes on handing the corpse out. The heartbeat in a XAML
	/// failure is what says which of those is happening -- an age climbing from the first injection is
	/// a wedge, not a busy app.
	/// </para>
	/// <para>
	/// So this key is a blast radius, not a lock over a resource, and it stays until injection stops
	/// wedging apps. Narrowing it trades wall clock for cascades.
	/// </para>
	/// </summary>
	public const string LiveApp = "LiveApp";

	/// <summary>
	/// Per probe, so tests on one queue behind each other for reasons of their own: the classic and
	/// modern apps are single-instance, and activating one while an instance runs foregrounds it rather
	/// than launching a process a debugger can attach to from birth.
	/// </summary>
	public const string WinUi = "WinUiProbe";

	/// <inheritdoc cref="WinUi"/>
	public const string ModernUwp = "ModernUwpProbe";
}


/// <summary>
/// The shared classic probe plus a scratch slot of this test's own, overlapping other slot tests.
/// </summary>
/// <remarks>
/// These derive from <see cref="NotInParallelAttribute"/> rather than declaring the constraint
/// themselves: TUnitAttribute's constructor is internal, so the framework's own attribute is the only
/// way in from outside its assembly. Deriving also means the keys are computed by a constructor rather
/// than written as attribute arguments, which is what lets a pool of them be passed at all -- an
/// attribute argument has to be a constant, and a string[] of sixteen cannot be.
/// </remarks>
internal sealed class ClassicSlotAttribute : NotInParallelAttribute
{
	public ClassicSlotAttribute(int slot)
		: base([ProbeKeys.ClassicSlot(slot), ProbeKeys.LiveApp]) => Order = ProbeKeys.SlotPhase;
}

/// <summary>The shared classic probe, to this test alone, for state with no owner smaller than the app.</summary>
internal sealed class ClassicSessionAttribute : NotInParallelAttribute
{
	public ClassicSessionAttribute()
		: base([.. ProbeKeys.AllClassicSlots, ProbeKeys.LiveApp]) => Order = ProbeKeys.SessionPhase;
}

/// <summary>
/// The classic probe to this test alone, which ends the shared app -- so these are admitted last,
/// after every test that could have used the app they are about to take away.
/// </summary>
internal sealed class ClassicOwnAppAttribute : NotInParallelAttribute
{
	public ClassicOwnAppAttribute()
		: base([.. ProbeKeys.AllClassicSlots, ProbeKeys.LiveApp]) => Order = ProbeKeys.OwnAppPhase;
}

/// <summary>The WinUI 3 probe, to this test alone.</summary>
internal sealed class WinUiProbeAttribute : NotInParallelAttribute
{
	public WinUiProbeAttribute() : base([ProbeKeys.WinUi, ProbeKeys.LiveApp])
	{
	}
}

/// <summary>The UWP-on-modern-.NET probe, to this test alone.</summary>
internal sealed class ModernUwpProbeAttribute : NotInParallelAttribute
{
	public ModernUwpProbeAttribute() : base([ProbeKeys.ModernUwp, ProbeKeys.LiveApp])
	{
	}
}
