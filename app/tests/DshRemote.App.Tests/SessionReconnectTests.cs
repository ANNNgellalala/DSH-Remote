using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DshRemote.App.ViewModels;
using DshRemote.Core;
using Xunit;

namespace DshRemote.App.Tests;

/// <summary>
/// Regression tests for the "follow subscription dropped" retry policy: a broken live subscription must be
/// retried at most five times (5 s apart in production) and then abandon the chat so the shell can return
/// to the connection page. Before this policy the retry loop was unbounded and, for the client-side
/// "already active subscription" error, failed instantly forever — the banner never stopped updating and
/// the chat page looked frozen.
/// </summary>
public sealed class SessionReconnectTests
{
    [Fact]
    public async Task FollowFailures_GiveUpAfterFiveRetries_AndRaiseAbandonedOnce()
    {
        var client = new ScriptedFollowClient(failuresBeforeSuccess: int.MaxValue);
        await using var session = await OpenAsync(client);
        var abandoned = 0;
        session.FollowAbandoned += (_, _) => Interlocked.Increment(ref abandoned);

        await session.LoadAsync();
        await WaitUntilAsync(() => session.IsFollowAbandoned);

        Assert.True(session.IsFollowAbandoned);
        Assert.Equal(SessionViewModel.MaxFollowRetries, session.FollowRetryCount);
        // One initial attempt plus exactly five retries.
        Assert.Equal(SessionViewModel.MaxFollowRetries + 1, client.FollowCalls);
        Assert.Equal(1, Volatile.Read(ref abandoned));
    }

    [Fact]
    public async Task FollowSuccessAfterTransientFailures_KeepsRetryingAndResetsBudget()
    {
        var client = new ScriptedFollowClient(failuresBeforeSuccess: 2);
        await using var session = await OpenAsync(client);
        session.FollowAbandoned += (_, _) => throw new Xunit.Sdk.XunitException("must not abandon a stream that recovers");

        await session.LoadAsync();
        await WaitUntilAsync(() => client.FollowCalls >= 3);
        await WaitUntilAsync(() => session.FollowRetryCount == 0);

        Assert.False(session.IsFollowAbandoned);
        Assert.Equal(3, client.FollowCalls);
    }

    [Fact]
    public async Task ReconnectCommand_AbandonsImmediately_WithoutRetrying()
    {
        var client = new ScriptedFollowClient(failuresBeforeSuccess: int.MaxValue);
        await using var session = await OpenAsync(client);
        var abandoned = 0;
        session.FollowAbandoned += (_, _) => Interlocked.Increment(ref abandoned);

        session.ReconnectCommand.Execute(null);
        await WaitUntilAsync(() => session.IsFollowAbandoned);

        Assert.Equal(1, Volatile.Read(ref abandoned));
        Assert.Equal(0, client.FollowCalls);
    }

    private static async Task<SessionViewModel> OpenAsync(IDshClient client)
    {
        var summary = new SessionSummary(
            "sess-retry",
            "重试测试",
            @"D:\Code\VibeCoding\DSHRemote",
            "deepseek-v4.1-flash",
            Running: false,
            AgentAvailable: true,
            Blank: false,
            UpdatedAt: 0,
            LastPromptAt: 0);
        var settings = new SettingsViewModel();
        var session = new SessionViewModel(
            client,
            summary,
            new RelayCommand(() => { }),
            new InteractionHub(),
            settings)
        {
            // Production waits 5 s between attempts; the state machine under test is the same without it.
            FollowRetryDelayOverride = (_, _) => Task.CompletedTask
        };
        await Task.Yield();
        return session;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("condition was not reached in time");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Minimal host: history/state are empty, the follow stream fails a scripted number of times and then
    /// stays open, delivering one event so the retry budget reset path is exercised.
    /// </summary>
    private sealed class ScriptedFollowClient : IDshClient, ISessionFollowClient, IHistoryPagingClient
    {
        private readonly int _failuresBeforeSuccess;
        private readonly Channel<SessionEventRecord> _live = Channel.CreateUnbounded<SessionEventRecord>();
        private int _followCalls;
        private long _seq;

        public ScriptedFollowClient(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int FollowCalls => Volatile.Read(ref _followCalls);

        public DshConnectionState State => DshConnectionState.Connected;

        public DshPairingInfo? Pairing => null;

#pragma warning disable CS0067 // connection state changes are not scripted
        public event EventHandler<DshConnectionState>? StateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(DshPairingInfo pairing, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionSummary>>(Array.Empty<SessionSummary>());

        public Task<SessionHistoryPage> GetHistoryAsync(
            string sessionId,
            long? beforeSeq = null,
            int maxMessages = 80,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionHistoryPage> GetLatestHistoryAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionHistoryPage> GetOlderHistoryAsync(
            string sessionId,
            long beforeSeq,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPage());

        public Task<SessionLiteSnapshot> OpenSessionLiteAsync(
            string sessionId,
            int maxMessages = 30,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLiteSnapshot(EmptyPage(), StateSnapshot()));

        public Task<SessionSnapshot> OpenSessionAsync(
            string sessionId,
            int maxMessages = 80,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionSnapshot(
                Array.Empty<SessionEventRecord>(),
                0,
                false,
                StateSnapshot()));

        public Task<SessionRuntimeState> GetStateAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(StateSnapshot());

        public Task<string> SendPromptAsync(string sessionId, string text, bool steer = false, CancellationToken cancellationToken = default)
            => Task.FromResult("req-1");

        public Task<string> SendPromptAsync(
            string sessionId,
            string text,
            IReadOnlyList<DshImageAttachment>? images,
            bool steer = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult("req-1");

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<SessionEventRecord> ObserveEventsAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var record in _live.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }

        public async IAsyncEnumerable<SessionEventRecord> FollowEventsAsync(
            string sessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _followCalls);
            if (attempt <= _failuresBeforeSuccess)
            {
                throw new DshRemoteException(
                    DshRemoteException.Codes.FollowBusy,
                    "a follow subscription for session 'sess-retry' is already active");
            }

            if (attempt == _failuresBeforeSuccess + 1)
            {
                // Deliver one record so the "stream is healthy again" reset runs.
                yield return new SessionEventRecord(
                    Interlocked.Increment(ref _seq),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "turn/start",
                    default,
                    "{}");
            }

            await foreach (var record in _live.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }

        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateQueueAsync(
            string sessionId,
            string itemId,
            QueueEditAction action,
            string? text = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static SessionHistoryPage EmptyPage() => new(Array.Empty<SessionEventRecord>(), 0, false, false, null);

        private static SessionRuntimeState StateSnapshot()
            => new(0, Active: true, Running: false, Array.Empty<QueuedPrompt>(), Array.Empty<string>());
    }
}
