using RoseMcp.Contracts;
using RoseMcp.Settings;
using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// How a tap dropped in from outside RoseMCP is found, admitted and chosen: read from its assembly's
/// metadata without loading it, refused where it would replace a tap RoseMCP ships or could never be
/// accepted, and chosen for a target by the endpoint listening in it.
/// </summary>
public sealed class TapDiscoveryTests
{
	private static readonly DroppedInTap Contoso = new()
	{
		Name = "Contoso",
		Module = "Contoso.dll",
		Endpoint = "contoso-diag-{pid}",
		EntryType = "Contoso.RoseTap.Tap",
		Protocol = 5,
		AssemblyPath = @"C:\taps\Contoso.RoseTap.dll",
	};

	/// <summary>
	/// The attribute exactly as a tap compiles it from the contract, with an assembly attribute in front: the
	/// tap's own copy, which is a type definition in the tap rather than a reference to anything RoseMCP has.
	/// </summary>
	private static string TapSource(string arguments) =>
		"using System;\n"
		+ $"[assembly: RoseMcp.Tap.Protocol.RoseMcpTap({arguments})]\n"
		+ File.ReadAllText(Checkout.RepositoryFile("external", "RoseMcp.Tap", "Protocol", "RoseMcpTapAttribute.cs"));

	[Test]
	public void A_tap_is_read_from_the_attribute_its_assembly_carries()
	{
		using var module = CompiledModule.Of(
			TapSource("""Name = "Contoso", Module = "Contoso.dll", Endpoint = "contoso-diag-{pid}", EntryType = "Contoso.RoseTap.Tap", Protocol = 5"""),
			"Contoso.RoseTap",
			withSymbols: false);

		var tap = TapManifest.Read(module.ModulePath).ShouldNotBeNull();

		tap.ShouldBe(Contoso with { AssemblyPath = Path.GetFullPath(module.ModulePath) });
	}

	[Test]
	public void An_assembly_without_the_attribute_is_not_a_tap()
	{
		using var module = CompiledModule.NestedLocals(withSymbols: false);

		TapManifest.Read(module.ModulePath).ShouldBeNull();
	}

	/// <summary>A tap's folder holds what it was built beside, native libraries included, and none of it is a tap.</summary>
	[Test]
	public void A_file_that_is_not_an_assembly_is_not_a_tap()
	{
		var path = Path.Combine(Path.GetTempPath(), $"not-a-tap-{Guid.NewGuid():N}.dll");
		File.WriteAllText(path, "MZ, and then nothing an image has");

		try
		{
			TapManifest.Read(path).ShouldBeNull();
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public void A_well_formed_tap_is_admitted()
	{
		DroppedInTaps.Refuse(Contoso, []).ShouldBeNull();
	}

	/// <summary>A stray assembly naming itself after a framework RoseMCP serves never replaces the tap that serves it.</summary>
	[Test]
	[Arguments("WinUI", "Uno.dll")]
	[Arguments("Uno", "Microsoft.WinUI.dll")]
	[Arguments("uwp", "Uno.dll")]
	[Arguments("Uno", "Windows.UI.Xaml.dll")]
	public void A_tap_claiming_a_framework_an_in_box_tap_serves_is_refused_naming_both(string name, string module)
	{
		var refusal = DroppedInTaps.Refuse(Contoso with { Name = name, Module = module }, []).ShouldNotBeNull();

		refusal.ShouldContain("RoseMCP's own");
		refusal.ShouldContain("Contoso.RoseTap.dll");
	}

	[Test]
	public void A_tap_whose_endpoint_names_no_process_is_refused()
	{
		DroppedInTaps.Refuse(Contoso with { Endpoint = "contoso-diag" }, []).ShouldNotBeNull().ShouldContain("{pid}");
	}

	[Test]
	public void A_tap_that_does_not_say_how_to_start_it_is_refused_saying_what_is_missing()
	{
		DroppedInTaps.Refuse(Contoso with { EntryType = string.Empty }, []).ShouldNotBeNull().ShouldContain("which type starts it");
	}

	/// <summary>
	/// The protocol a tap's attribute declares is checked before it is loaded into anything, so one built against a
	/// contract this host could never accept is refused here rather than after it has been started in somebody's app.
	/// </summary>
	[Test]
	public void A_tap_speaking_a_protocol_outside_the_dropped_in_range_is_refused_saying_which_side_to_update()
	{
		DroppedInTaps.Refuse(Contoso with { Protocol = 4 }, []).ShouldNotBeNull().ShouldContain("Rebuild the tap");
		DroppedInTaps.Refuse(Contoso with { Protocol = TapProtocolRange.DroppedIn.Maximum + 1 }, []).ShouldNotBeNull().ShouldContain("has to be updated");
	}

	[Test]
	public void The_first_tap_found_for_a_framework_keeps_it()
	{
		var second = Contoso with { AssemblyPath = @"D:\build\Contoso.RoseTap.dll" };

		var refusal = DroppedInTaps.Refuse(second, [Contoso]).ShouldNotBeNull();

		refusal.ShouldContain(Contoso.AssemblyPath);
		refusal.ShouldContain(second.AssemblyPath);
	}

	[Test]
	public void The_tap_whose_endpoint_is_listening_in_the_target_serves_it()
	{
		var avalonia = Contoso with { Name = "Avalonia", Module = "Avalonia.Base.dll", Endpoint = "avalonia-diag-{pid}" };

		var (tap, reason) = DroppedInTaps.Choose([Contoso, avalonia], 4120, [], pipe => pipe == "avalonia-diag-4120", "No in-box tap.", []);

		tap.ShouldBe(avalonia);
		reason.ShouldBeNull();
	}

	/// <summary>
	/// The ordinary state of a game built without its framework's diagnostics switched on, said as exactly that, so
	/// the reader goes to the game's build rather than looking for a tap that is already there.
	/// </summary>
	[Test]
	public void A_target_running_a_tap_framework_with_no_endpoint_listening_is_told_so()
	{
		var (tap, reason) = DroppedInTaps.Choose([Contoso], 4120, ["kernel32.dll", "contoso.dll"], _ => false, "No in-box tap.", []);

		tap.ShouldBeNull();
		reason.ShouldNotBeNull().ShouldContain("contoso-diag-4120");
		reason.ShouldContain("diagnostics switched on");
	}

	[Test]
	public void A_target_no_tap_serves_names_the_folders_that_were_read()
	{
		var (tap, reason) = DroppedInTaps.Choose([], 4120, [], _ => false, "No in-box tap.", [@"C:\taps"]);

		tap.ShouldBeNull();
		reason.ShouldBe(@"No in-box tap. No tap dropped into C:\taps serves it either.");
	}

	/// <summary>
	/// Taps are looked for beside the settings file first, then where the settings say, then where the environment
	/// says, and a folder named twice is read once.
	/// </summary>
	[Test]
	public void Tap_folders_are_read_beside_the_settings_then_from_the_settings_then_from_the_environment()
	{
		var localAppData = Path.Combine(Path.GetTempPath(), $"rose-taps-{Guid.NewGuid():N}");
		var fromSettings = Path.Combine(localAppData, "build");
		var fromVariable = Path.Combine(localAppData, "session");
		var settings = new RoseSettings { TapFolders = [fromSettings] };

		var folders = TapFolders.For(settings, localAppData, string.Join(Path.PathSeparator, fromVariable, fromSettings));

		folders.ShouldBe(
		[
			Path.Combine(localAppData, "BinaryVibrance", "RoseMCP", "taps"),
			fromSettings,
			fromVariable,
		]);
	}
}
