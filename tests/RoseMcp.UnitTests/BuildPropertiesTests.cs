using System.Runtime.InteropServices;

using RoseMcp.Solutions;

namespace RoseMcp.UnitTests;

public sealed class BuildPropertiesTests
{
	[Test]
	public void Leaves_msbuild_alone_when_the_solution_declares_nothing()
	{
		var build = BuildProperties.Select(Options(), SolutionConfigurations.None);

		build.Configuration.ShouldBeNull();
		build.Platform.ShouldBeNull();
		build.AsGlobalProperties().ShouldBeEmpty();
		build.Notice.ShouldBeNull();
	}

	[Test]
	public void Leaves_msbuild_alone_when_the_solution_declares_the_defaults()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug", "Release"],
			Platforms = ["AnyCPU", "x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		build.Configuration.ShouldBeNull();
		build.Platform.ShouldBeNull();
		build.Notice.ShouldBeNull();
	}

	/// <summary>
	/// A chosen platform has to be marked as chosen, because the wrong one is survivable and therefore
	/// silent: the projects load, and only the in-solution references quietly fail to resolve. Nothing
	/// downstream can say so unless it knows the value was a guess.
	/// </summary>
	[Test]
	public void Records_that_a_platform_was_chosen_rather_than_asked_for()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug", "Release"],
			Platforms = ["x64", "ARM64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		build.PlatformWasChosen.ShouldBeTrue();
	}

	[Test]
	public void Does_not_call_a_platform_chosen_when_the_caller_named_it()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug"],
			Platforms = ["x64", "ARM64"],
		};

		var options = new WorkerOptions { SolutionPath = "S.slnx", Platform = "x64" };
		var build = BuildProperties.Select(options, declared);

		build.Platform.ShouldBe("x64");
		build.PlatformWasChosen.ShouldBeFalse("a platform the caller named was not chosen by this");
	}

	/// <summary>
	/// Nor when there was nothing to choose. A solution declaring AnyCPU leaves MSBuild's default
	/// alone, and a default is not a guess.
	/// </summary>
	[Test]
	public void Does_not_call_msbuilds_own_default_a_chosen_platform()
	{
		var build = BuildProperties.Select(Options(), SolutionConfigurations.None);

		build.PlatformWasChosen.ShouldBeFalse("MSBuild's own default is not a choice this made");
	}

	/// <summary>
	/// #24: a chosen platform that turns out to be wrong resolves no in-solution references and does
	/// not fail. Every project reports loaded, because each resolved the framework; what they did not
	/// resolve is each other. The unresolved paths are the only evidence there is.
	/// </summary>
	[Test]
	public void Suspects_the_platform_it_chose_when_the_unresolved_paths_are_under_it()
	{
		var build = Chose("ARM64", ["x64", "ARM64"]);

		var suspicion = build.SuspectWrongPlatform(
		[
			@"Cannot resolve Assembly or Windows Metadata file 'D:\repo\A\bin\ARM64\Debug\A.dll'",
			@"Cannot resolve Assembly or Windows Metadata file 'D:\repo\B\bin\ARM64\Debug\B.dll'",
		]);

		suspicion.ShouldNotBeNull();
		suspicion.ShouldContain("'ARM64' was chosen", Case.Sensitive);
		suspicion.ShouldContain("2 load diagnostic(s)", Case.Sensitive);

		// And it names the way out, which is the other platform rather than a general instruction.
		suspicion.ShouldContain("platform=x64", Case.Sensitive);
	}

	/// <summary>
	/// A caller who named the platform has already decided. Telling them their own answer looks wrong
	/// is a different and much noisier thing, and this is a degraded reason -- it has to stay rare.
	/// </summary>
	[Test]
	public void Says_nothing_about_a_platform_the_caller_asked_for()
	{
		var build = Chose("ARM64", ["x64", "ARM64"]) with { PlatformWasChosen = false };

		build.SuspectWrongPlatform([@"Cannot resolve 'D:\repo\A\bin\ARM64\Debug\A.dll'"]).ShouldBeNull();
	}

	/// <summary>
	/// The choice being right is the ordinary case, and it must be silent: a solution that declares
	/// only ARM64 on an ARM64 machine with everything built is not degraded.
	/// </summary>
	[Test]
	public void Says_nothing_when_no_unresolved_path_is_under_the_chosen_platform()
	{
		var build = Chose("ARM64", ["x64", "ARM64"]);

		build.SuspectWrongPlatform(
		[
			"Found project reference without a matching metadata reference: A.csproj",
			@"Cannot resolve Assembly or Windows Metadata file 'D:\repo\A\bin\x64\Debug\A.dll'",
		]).ShouldBeNull();
	}

	/// <summary>Posix separators too, so this reads the same on Linux.</summary>
	[Test]
	public void Recognises_the_chosen_platform_in_a_posix_path()
	{
		var build = Chose("ARM64", ["x64", "ARM64"]);

		build.SuspectWrongPlatform(["Cannot resolve '/home/me/repo/A/bin/ARM64/Debug/A.dll'"]).ShouldNotBeNull();
	}

	/// <summary>
	/// A platform name appearing in prose is not a path under it. The suspicion is a degraded reason,
	/// so a false one costs the word its meaning.
	/// </summary>
	[Test]
	public void Does_not_take_the_platform_name_in_prose_for_an_output_path()
	{
		var build = Chose("ARM64", ["x64", "ARM64"]);

		build.SuspectWrongPlatform(["ARM64 support for this SDK is preview."]).ShouldBeNull();
	}

	private static BuildProperties Chose(string platform, string[] declared) => new()
	{
		Platform = platform,
		PlatformWasChosen = true,
		Available = new SolutionConfigurations { Platforms = declared },
	};

	[Test]
	public void Picks_a_declared_configuration_when_the_solution_has_no_plain_Debug()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug-2024", "Debug-2025", "Release"],
			Platforms = ["x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		build.Configuration.ShouldBe("Debug-2024");
		build.Platform.ShouldBe("x64");
		build.Describe().ShouldBe("Debug-2024|x64");

		// The notice has to name both the choice and the alternatives, because the choice is a guess
		// and the caller is the only one who can correct it.
		build.Notice!.ShouldContain("Debug-2024", Case.Sensitive);
		build.Notice!.ShouldContain("Debug-2025", Case.Sensitive);
		build.Notice!.ShouldContain("x64", Case.Sensitive);
	}

	/// <summary>
	/// A solution build takes the first configuration with the first platform, which is how a solution
	/// listing ARM64 first comes to build ARM64 on an x64 machine. Nothing is executed during a load,
	/// so the wrong platform is survivable -- but it changes conditional compilation, and matching the
	/// machine is what a person expects.
	/// </summary>
	[Test]
	public void Prefers_this_machines_architecture_over_whatever_is_declared_first()
	{
		var host = RuntimeInformation.OSArchitecture.ToString();

		// Whichever machine this runs on, something else is declared first. Naming ARM64 as the
		// wrong answer outright was fine until the machine running the tests was one.
		var listedFirst = host.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "x64" : "ARM64";

		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug"],
			Platforms = [listedFirst, "x86", host],
		};

		var build = BuildProperties.Select(Options(), declared);

		build.Platform.ShouldBe(host, StringCompareShould.IgnoreCase);
		build.Platform.ShouldNotBe(listedFirst, StringComparer.OrdinalIgnoreCase);
	}

	[Test]
	public void Falls_back_to_the_first_declared_platform_when_the_machines_is_not_offered()
	{
		var declared = new SolutionConfigurations { Configurations = ["Debug"], Platforms = ["Itanium", "MIPS"] };

		var build = BuildProperties.Select(Options(), declared);

		build.Platform.ShouldBe("Itanium");
	}

	[Test]
	public void Honours_a_requested_configuration_the_solution_does_not_declare()
	{
		var declared = new SolutionConfigurations { Configurations = ["Debug-2024"], Platforms = ["x64"] };
		var options = new WorkerOptions { SolutionPath = "S.slnx", Configuration = "Debug-2027" };

		var build = BuildProperties.Select(options, declared);

		build.Configuration.ShouldBe("Debug-2027");
		build.Notice!.ShouldContain("Debug-2027", Case.Sensitive);
		build.Notice!.ShouldContain("not one this solution declares", Case.Sensitive);
	}

	[Test]
	public void Carries_pinned_properties_into_both_the_build_and_the_restore()
	{
		var options = new WorkerOptions
		{
			SolutionPath = "S.slnx",
			Configuration = "Release",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var build = BuildProperties.Select(options, SolutionConfigurations.None);

		build.AsGlobalProperties()["RevitVersion"].ShouldBe("2027");
		build.AsGlobalProperties()["Configuration"].ShouldBe("Release");
		build.AsRestoreArguments().ShouldContain("-p:RevitVersion=2027");
		build.Describe().ShouldContain("RevitVersion=2027", Case.Sensitive);
	}

	[Test]
	public void Takes_what_a_config_file_pins()
	{
		var pinned = new WorkspaceConfigFile
		{
			Path = "/repo/rosemcp.json",
			Configuration = "Debug-2027",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var declared = new SolutionConfigurations { Configurations = ["Debug-2024", "Debug-2027"], Platforms = ["x64"] };

		var build = BuildProperties.Select(Options(), declared, pinned);

		build.Configuration.ShouldBe("Debug-2027");
		build.AsGlobalProperties()["RevitVersion"].ShouldBe("2027");

		// Which file did it, because a caller told what it was loaded under has to be able to find
		// the thing that decided.
		build.Notice!.ShouldContain("rosemcp.json", Case.Sensitive);
	}

	[Test]
	public void Prefers_what_was_asked_for_over_what_a_config_file_pins()
	{
		var pinned = new WorkspaceConfigFile
		{
			Path = "/repo/rosemcp.json",
			Configuration = "Debug-2024",
			Platform = "x64",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2024", ["Extra"] = "kept" },
		};

		var options = new WorkerOptions
		{
			SolutionPath = "S.slnx",
			Configuration = "Debug-2027",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var build = BuildProperties.Select(options, SolutionConfigurations.None, pinned);

		build.Configuration.ShouldBe("Debug-2027");
		build.AsGlobalProperties()["RevitVersion"].ShouldBe("2027");

		// Merged rather than replaced: overriding one property does not discard the rest.
		build.AsGlobalProperties()["Extra"].ShouldBe("kept");
		build.Platform.ShouldBe("x64");
	}

	[Test]
	public void Finds_a_config_file_beside_the_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");

			var found = WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx"));

			found.ShouldNotBeNull();
			found.Configuration.ShouldBe("Debug-2027");
			found.Properties["RevitVersion"].ShouldBe("2027");
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// Two solutions in one directory can want different things, which is the ordinary case and not a
	/// corner: a Revit add-in solution declaring Debug-2024 through Debug-2027 sits beside an
	/// installer solution declaring no build types at all. A file named after one of them must not
	/// speak for the other.
	/// </summary>
	[Test]
	public void Prefers_the_file_named_after_this_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");
			File.WriteAllText(
				Path.Combine(root.FullName, WorkspaceConfigFile.NameFor("Installer.slnx")),
				"{ \"configuration\": \"Debug-2024\" }");

			(WorkspaceConfigFile.Find(Path.Combine(root.FullName, "Installer.slnx"))?.Configuration).ShouldBe(
				"Debug-2024");

			(WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx"))?.Configuration).ShouldBe(
				"Debug-2027");
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	/// <summary>
	/// Not found by walking up, unlike MSBuild's and NuGet's own files. Configurations belong to a
	/// solution rather than to a tree, so a file at a repository root would be a guess applied to
	/// every solution beneath it.
	/// </summary>
	[Test]
	public void Ignores_a_config_file_above_the_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");
			var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "app"));

			WorkspaceConfigFile.Find(Path.Combine(nested.FullName, "App.slnx")).ShouldBeNull();
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	[Test]
	public void Treats_an_unreadable_config_file_as_absent()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ not json");

			WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx")).ShouldBeNull();
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	private static WorkerOptions Options() => new() { SolutionPath = "S.slnx" };
}
