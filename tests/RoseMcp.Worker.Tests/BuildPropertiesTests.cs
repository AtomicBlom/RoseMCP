using System.Runtime.InteropServices;

namespace RoseMcp.Worker.Tests;

public sealed class BuildPropertiesTests
{
	[Fact]
	public void Leaves_msbuild_alone_when_the_solution_declares_nothing()
	{
		var build = BuildProperties.Select(Options(), SolutionConfigurations.None);

		Assert.Null(build.Configuration);
		Assert.Null(build.Platform);
		Assert.Empty(build.AsGlobalProperties());
		Assert.Null(build.Notice);
	}

	[Fact]
	public void Leaves_msbuild_alone_when_the_solution_declares_the_defaults()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug", "Release"],
			Platforms = ["AnyCPU", "x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		Assert.Null(build.Configuration);
		Assert.Null(build.Platform);
		Assert.Null(build.Notice);
	}

	[Fact]
	public void Picks_a_declared_configuration_when_the_solution_has_no_plain_Debug()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug-2024", "Debug-2025", "Release"],
			Platforms = ["x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		Assert.Equal("Debug-2024", build.Configuration);
		Assert.Equal("x64", build.Platform);
		Assert.Equal("Debug-2024|x64", build.Describe());

		// The notice has to name both the choice and the alternatives, because the choice is a guess
		// and the caller is the only one who can correct it.
		Assert.Contains("Debug-2024", build.Notice);
		Assert.Contains("Debug-2025", build.Notice);
		Assert.Contains("x64", build.Notice);
	}

	/// <summary>
	/// A solution build takes the first configuration with the first platform, which is how a solution
	/// listing ARM64 first comes to build ARM64 on an x64 machine. Nothing is executed during a load,
	/// so the wrong platform is survivable -- but it changes conditional compilation, and matching the
	/// machine is what a person expects.
	/// </summary>
	[Fact]
	public void Prefers_this_machines_architecture_over_whatever_is_declared_first()
	{
		var host = RuntimeInformation.OSArchitecture.ToString();
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug"],
			Platforms = ["ARM64", "x86", "x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		// Whichever architecture this test is running on, that is the one that should be chosen --
		// and it is deliberately not the first in the list.
		Assert.Equal(host, build.Platform, ignoreCase: true);
		Assert.NotEqual("ARM64", build.Platform, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Falls_back_to_the_first_declared_platform_when_the_machines_is_not_offered()
	{
		var declared = new SolutionConfigurations { Configurations = ["Debug"], Platforms = ["Itanium", "MIPS"] };

		var build = BuildProperties.Select(Options(), declared);

		Assert.Equal("Itanium", build.Platform);
	}

	[Fact]
	public void Honours_a_requested_configuration_the_solution_does_not_declare()
	{
		var declared = new SolutionConfigurations { Configurations = ["Debug-2024"], Platforms = ["x64"] };
		var options = new WorkerOptions { SolutionPath = "S.slnx", Configuration = "Debug-2027" };

		var build = BuildProperties.Select(options, declared);

		Assert.Equal("Debug-2027", build.Configuration);
		Assert.Contains("Debug-2027", build.Notice);
		Assert.Contains("not one this solution declares", build.Notice);
	}

	[Fact]
	public void Carries_pinned_properties_into_both_the_build_and_the_restore()
	{
		var options = new WorkerOptions
		{
			SolutionPath = "S.slnx",
			Configuration = "Release",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var build = BuildProperties.Select(options, SolutionConfigurations.None);

		Assert.Equal("2027", build.AsGlobalProperties()["RevitVersion"]);
		Assert.Equal("Release", build.AsGlobalProperties()["Configuration"]);
		Assert.Contains("-p:RevitVersion=2027", build.AsRestoreArguments());
		Assert.Contains("RevitVersion=2027", build.Describe());
	}

	[Fact]
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

		Assert.Equal("Debug-2027", build.Configuration);
		Assert.Equal("2027", build.AsGlobalProperties()["RevitVersion"]);

		// Which file did it, because a caller told what it was loaded under has to be able to find
		// the thing that decided.
		Assert.Contains("rosemcp.json", build.Notice);
	}

	[Fact]
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

		Assert.Equal("Debug-2027", build.Configuration);
		Assert.Equal("2027", build.AsGlobalProperties()["RevitVersion"]);

		// Merged rather than replaced: overriding one property does not discard the rest.
		Assert.Equal("kept", build.AsGlobalProperties()["Extra"]);
		Assert.Equal("x64", build.Platform);
	}

	[Fact]
	public void Finds_a_config_file_beside_the_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");

			var found = WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx"));

			Assert.NotNull(found);
			Assert.Equal("Debug-2027", found.Configuration);
			Assert.Equal("2027", found.Properties["RevitVersion"]);
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
	[Fact]
	public void Prefers_the_file_named_after_this_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");
			File.WriteAllText(
				Path.Combine(root.FullName, WorkspaceConfigFile.NameFor("Installer.slnx")),
				"{ \"configuration\": \"Debug-2024\" }");

			Assert.Equal(
				"Debug-2024",
				WorkspaceConfigFile.Find(Path.Combine(root.FullName, "Installer.slnx"))?.Configuration);

			Assert.Equal(
				"Debug-2027",
				WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx"))?.Configuration);
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
	[Fact]
	public void Ignores_a_config_file_above_the_solution()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ \"configuration\": \"Debug-2027\", \"properties\": { \"RevitVersion\": \"2027\" } }");
			var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "app"));

			Assert.Null(WorkspaceConfigFile.Find(Path.Combine(nested.FullName, "App.slnx")));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	[Fact]
	public void Treats_an_unreadable_config_file_as_absent()
	{
		var root = Directory.CreateTempSubdirectory("rosemcp-config-");
		try
		{
			File.WriteAllText(Path.Combine(root.FullName, WorkspaceConfigFile.FileName), "{ not json");

			Assert.Null(WorkspaceConfigFile.Find(Path.Combine(root.FullName, "App.slnx")));
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	private static WorkerOptions Options() => new() { SolutionPath = "S.slnx" };
}
