namespace DshRemote.App.Tests;

/// <summary>
/// Name of the xUnit collection serializing tests that mutate process-wide state
/// (<c>AppHost.StateDirectoryProvider</c>, <c>PlatformServices.SecretStore</c>). Tests in the same
/// collection never run in parallel, which keeps their temporary state directories private.
/// </summary>
internal static class AppHostStateCollection
{
    public const string Name = "DshRemote AppHost state";
}
