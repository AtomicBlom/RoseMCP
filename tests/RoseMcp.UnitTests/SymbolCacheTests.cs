using RoseMcp.Symbols;

namespace RoseMcp.UnitTests;

/// <summary>
/// The cache between a stack walk and the files it reads.
/// <para>
/// Two properties, pulling opposite ways, and both matter. Reading one module once is what keeps a
/// twenty-frame walk with locals from opening the same handful of files forty times, on the path a
/// person is waiting on. Noticing that the file has changed is what stops a session naming lines out
/// of a build that has been replaced -- a loaded module cannot be rebuilt under a running target,
/// but this cache outlives targets.
/// </para>
/// </summary>
public sealed class SymbolCacheTests
{
	[Test]
	public void One_read_serves_every_ask_for_the_same_module()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var cache = new SymbolCache();

		var first = cache.For(fixture.ModulePath);

		Assert.NotNull(first);
		Assert.Same(first, cache.For(fixture.ModulePath));
	}

	/// <summary>
	/// A build that lands while this process lives leaves a different file under the same path, and
	/// answering from the last read would name lines out of code that is gone.
	/// </summary>
	[Test]
	public void A_module_written_again_is_read_again()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var cache = new SymbolCache();

		var first = cache.For(fixture.ModulePath);
		Assert.NotNull(first);

		File.SetLastWriteTimeUtc(fixture.ModulePath, File.GetLastWriteTimeUtc(fixture.ModulePath).AddMinutes(1));

		var second = cache.For(fixture.ModulePath);
		Assert.NotNull(second);
		Assert.NotSame(first, second);
	}

	/// <summary>
	/// A path that is not an assembly is remembered as unreadable, so it is not re-opened on every
	/// frame of every stack. The native modules on a managed stack are the ordinary case of this.
	/// </summary>
	[Test]
	public void A_path_that_cannot_be_read_stays_read_as_nothing()
	{
		using var cache = new SymbolCache();
		var missing = Path.Combine(Path.GetTempPath(), $"rose-absent-{Guid.NewGuid():n}.dll");

		Assert.Null(cache.For(missing));
		Assert.Null(cache.For(missing));
	}

	/// <summary>
	/// A module that was not there when it was first asked about is read when it appears, because
	/// absent is stamped as absent rather than as some real file's write time.
	/// </summary>
	[Test]
	public void A_module_that_appears_later_is_read()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var cache = new SymbolCache();
		var appearing = Path.Combine(Path.GetDirectoryName(fixture.ModulePath)!, "Appearing.dll");

		Assert.Null(cache.For(appearing));

		File.Copy(fixture.ModulePath, appearing);

		Assert.NotNull(cache.For(appearing));
	}

	/// <summary>Evicting is for a caller that knows the output has just been replaced.</summary>
	[Test]
	public void Evicting_a_module_reads_it_again()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var cache = new SymbolCache();

		var first = cache.For(fixture.ModulePath);
		Assert.NotNull(first);

		cache.Evict(fixture.ModulePath);

		var second = cache.For(fixture.ModulePath);
		Assert.NotNull(second);
		Assert.NotSame(first, second);

		// Evicting something never read is a no-op rather than a throw: a caller telling the cache
		// about a rebuild does not know what it has read.
		cache.Evict(Path.Combine(Path.GetTempPath(), "rose-never-read.dll"));
	}

	/// <summary>
	/// Nothing here holds a handle on the file it read. A live-app host lives for hours beside a
	/// developer who is rebuilding, and a held handle on their output fails their next build.
	/// </summary>
	[Test]
	public void Reading_a_module_leaves_it_deletable()
	{
		using var fixture = CompiledModule.NestedLocals();
		using var cache = new SymbolCache();

		Assert.NotNull(cache.For(fixture.ModulePath));

		File.Delete(fixture.ModulePath);
		File.Delete(fixture.PdbPath);
	}
}
