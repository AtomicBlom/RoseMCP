using System.Security.Cryptography;
using System.Text.Json;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// The version of an operator view: a hash of what it holds, as the operator API would write it, so two reads that
/// would show a person the same thing carry the same version and any difference changes it.
/// <para>
/// The caller hands in the view with whatever changes on every read taken out -- an uptime, an idle time, a memory
/// figure -- since a version that changed every second would end every wait at once. Sixteen hex digits: a
/// collision would only make one wait run to its end, so this is plenty.
/// </para>
/// </summary>
public static class ViewVersion
{
	/// <summary>The version of <paramref name="stable"/>, the view without what changes on every read.</summary>
	public static string Of<T>(T stable) =>
		Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stable, ContractJson.Options)).AsSpan(0, 8));
}
