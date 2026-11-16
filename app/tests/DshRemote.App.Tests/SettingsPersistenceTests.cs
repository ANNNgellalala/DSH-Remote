using DshRemote.App.Platform;
using DshRemote.App.Services;
using DshRemote.App.ViewModels;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for the settings-page preferences (full-screen composer). They live in
/// <c>settings.json</c>; the Android head additionally keeps a pairing snapshot in the Keystore secret
/// store, and that snapshot is only written when a connection succeeds. Restoring a stale snapshot at
/// startup must not roll the preferences back to their connect-time values.
/// </summary>
/// <remarks>
/// Shares the process-wide <see cref="AppHost.StateDirectoryProvider"/> / <see cref="PlatformServices.SecretStore"/>
/// with <see cref="WorkspaceSelectionTests"/>, so both classes are serialized in one xUnit collection.
/// </remarks>
[Collection(AppHostStateCollection.Name)]
public sealed class SettingsPersistenceTests : IDisposable
{
    private const string PairingSecretKey = "dsh.pairing";

    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "dshremote-settings-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeSecretStore _secretStore = new();

    public SettingsPersistenceTests()
    {
        Directory.CreateDirectory(_stateDirectory);
        AppHost.StateDirectoryProvider = () => _stateDirectory;
        PlatformServices.SecretStore = _secretStore;
    }

    public void Dispose()
    {
        AppHost.StateDirectoryProvider = null;
        PlatformServices.SecretStore = null;
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch
        {
            // best effort: the test result is what matters.
        }
    }

    [Fact]
    public void Preferences_RoundTripThroughSettingsFile()
    {
        var first = new SettingsViewModel();
        first.ExpandComposerFullscreen = true;

        var second = new SettingsViewModel();
        second.Load();

        Assert.True(second.ExpandComposerFullscreen);
    }

    /// <summary>
    /// The bug: on every cold start the pairing snapshot (written at connect time, preferences still at
    /// their defaults) was replayed into the view model. The preference setters persist on change, so the
    /// replayed values were written over settings.json before <see cref="SettingsViewModel.ReloadPreferences"/>
    /// could read the live file back — the settings page always looked reset after a restart.
    /// </summary>
    [Fact]
    public async Task StartupRestore_DoesNotRollPreferencesBackToTheConnectTimeSnapshot()
    {
        var pairing = new DshPairingInfo("192.0.2.20", 8443, "token-1", "spki-1", "Desk");

        // Session 1: pair, snapshot the pairing document (preferences still off), then turn them on.
        var session = new SettingsViewModel();
        session.ApplyPairing(pairing);
        var connectTimeSnapshot = session.ToSecretJson();
        Assert.False(string.IsNullOrWhiteSpace(connectTimeSnapshot));

        session.ExpandComposerFullscreen = true;

        // Session 2 (cold start): settings.json holds the user's preferences, the Keystore holds the
        // older pairing snapshot. This mirrors what MainViewModel.InitializePlatformAsync does.
        _secretStore.Values[PairingSecretKey] = connectTimeSnapshot!;
        var viewModel = new MainViewModel();
        await viewModel.InitializePlatformAsync();

        Assert.Equal("192.0.2.20", viewModel.Settings.Host);
        Assert.Equal(8443, int.Parse(viewModel.Settings.Port));
        Assert.True(
            viewModel.Settings.ExpandComposerFullscreen,
            "the full-screen composer preference must survive the stale pairing snapshot");

        // And the persisted file keeps the user's values for the next restart.
        var reloaded = new SettingsViewModel();
        reloaded.Load();
        Assert.True(reloaded.ExpandComposerFullscreen);
    }

    /// <summary>
    /// The pairing snapshot still restores the connection fields (that is its purpose) when settings.json
    /// no longer carries them.
    /// </summary>
    [Fact]
    public async Task StartupRestore_StillRestoresThePairingFromTheSecretStore()
    {
        var session = new SettingsViewModel();
        session.ApplyPairing(new DshPairingInfo("10.0.0.5", 9443, "token-9", "spki-9", "Lab"));
        _secretStore.Values[PairingSecretKey] = session.ToSecretJson()!;

        var viewModel = new MainViewModel();
        await viewModel.InitializePlatformAsync();

        Assert.Equal("10.0.0.5", viewModel.Settings.Host);
        Assert.Equal("9443", viewModel.Settings.Port);
        Assert.Equal("token-9", viewModel.Settings.Token);
        Assert.Equal("spki-9", viewModel.Settings.SpkiSha256);
        Assert.Equal("Lab", viewModel.Settings.DesktopName);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key)
            => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
