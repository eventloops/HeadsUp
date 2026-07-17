using HeadsUp.Configuration;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Wpf.Tests;

public class ConnectionGateTests
{
    [Fact]
    public void Opens_and_waits_complete()
    {
        var gate = new ConnectionGate();
        Assert.False(gate.IsOpen);

        gate.Open();

        Assert.True(gate.IsOpen);
        Assert.True(gate.WaitAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task Waits_block_until_opened()
    {
        var gate = new ConnectionGate();
        var wait = gate.WaitAsync(CancellationToken.None);

        Assert.False(wait.IsCompleted);
        gate.Open();
        await wait.WaitAsync(TimeSpan.FromSeconds(2)); // completes now
    }

    [Fact]
    public async Task Reset_re_arms_the_gate()
    {
        var gate = new ConnectionGate();
        gate.Open();
        Assert.True(gate.IsOpen);

        gate.Reset();
        Assert.False(gate.IsOpen);

        var wait = gate.WaitAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted); // blocks again until re-opened

        gate.Open();
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public class WelcomeViewModelTests
{
    private sealed class StubAlertStore : IAlertSettingsStore
    {
        public AlertOptions Current { get; set; } = new();
        public AlertOptions? LastSaved { get; private set; }
        public Task SaveAsync(AlertOptions options, CancellationToken ct = default)
        {
            LastSaved = options;
            return Task.CompletedTask;
        }
    }

    private readonly StubAlertStore _store = new();
    private readonly AppStatus _status = new();

    private WelcomeViewModel Create() => new(_store, _status);

    [Fact]
    public void Loads_toggle_defaults_from_the_store_without_saving()
    {
        _store.Current = new AlertOptions { Huddles = true, Mentions = false, DirectMessages = true, Keywords = false };
        var vm = Create();

        Assert.True(vm.NotifyHuddles);
        Assert.False(vm.NotifyMentions);
        Assert.True(vm.NotifyDirectMessages);
        Assert.Null(_store.LastSaved);
    }

    [Fact]
    public void Toggling_persists_and_preserves_keywords_and_muted()
    {
        _store.Current = new AlertOptions
        {
            KeywordList = { "deploy" },
            MutedChannels = { new MutedChannel { Id = "C1", Name = "random" } }
        };
        var vm = Create();

        vm.NotifyKeywords = true;

        Assert.True(_store.LastSaved!.Keywords);
        Assert.Equal(new[] { "deploy" }, _store.LastSaved.KeywordList);
        Assert.Single(_store.LastSaved.MutedChannels);
    }

    [Fact]
    public void Connect_enters_connecting_state_and_requests_connect_without_closing()
    {
        var vm = Create();
        var connect = 0; var dismissed = 0; var close = 0;
        vm.ConnectRequested += (_, _) => connect++;
        vm.Dismissed += (_, _) => dismissed++;
        vm.CloseRequested += (_, _) => close++;

        vm.ConnectCommand.Execute(null);

        Assert.True(vm.IsConnecting);
        Assert.Equal(1, connect);
        Assert.Equal(0, dismissed);
        Assert.Equal(0, close); // stays open in the connecting state
    }

    [Fact]
    public void Becoming_connected_while_connecting_requests_close()
    {
        var vm = Create();
        var close = 0;
        vm.CloseRequested += (_, _) => close++;

        vm.ConnectCommand.Execute(null);
        _status.SetConnection(true, "Watching for huddles");

        Assert.Equal(1, close);
    }

    [Fact]
    public void Sign_in_window_coming_up_requests_close()
    {
        var vm = Create();
        var close = 0;
        vm.CloseRequested += (_, _) => close++;

        vm.ConnectCommand.Execute(null);
        _status.NeedsSignIn = true;

        Assert.Equal(1, close);
    }

    [Fact]
    public void Status_changes_before_connecting_are_ignored()
    {
        var vm = Create();
        var close = 0;
        vm.CloseRequested += (_, _) => close++;

        _status.SetConnection(true, "up"); // not connecting yet
        _status.NeedsSignIn = true;

        Assert.Equal(0, close);
    }

    [Fact]
    public void Later_dismisses()
    {
        var vm = Create();
        var dismissed = 0;
        vm.Dismissed += (_, _) => dismissed++;

        vm.LaterCommand.Execute(null);

        Assert.Equal(1, dismissed);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public void Dispose_stops_reacting_to_status()
    {
        var vm = Create();
        var close = 0;
        vm.CloseRequested += (_, _) => close++;
        vm.ConnectCommand.Execute(null);

        vm.Dispose();
        _status.SetConnection(true, "up");

        Assert.Equal(0, close);
    }
}
