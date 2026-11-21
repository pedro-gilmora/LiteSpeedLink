using SourceCrafter.DependencyInjection.Attributes;
using SourceCrafter.LiteSpeedLink;

namespace WinChat;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ChatForm());
    }
}

sealed class ChatForm : Form
{
    readonly TextBox _server = new() { Text = "localhost", Width = 120 };
    readonly TextBox _user = new() { Text = Environment.UserName, Width = 100 };
    readonly TextBox _room = new() { Text = "lobby", Width = 100 };
    readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    readonly ListBox _messages = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox _input = new() { Dock = DockStyle.Fill, Enabled = false };
    readonly Button _send = new() { Text = "Send", Dock = DockStyle.Right, Enabled = false };

    ChatConnection? _connection;
    CancellationTokenSource? _stop;

    public ChatForm()
    {
        Text = "WinChat";
        ClientSize = new(560, 400);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.AddRange([new Label { Text = "Server", AutoSize = true }, _server, new Label { Text = "User", AutoSize = true }, _user,
            new Label { Text = "Room", AutoSize = true }, _room, _connect]);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        bottom.Controls.AddRange([_input, _send]);

        Controls.AddRange([_messages, bottom, top]);
        AcceptButton = _send;

        _connect.Click += async (_, _) => await ToggleAsync();
        _send.Click += async (_, _) => await SendAsync();
    }

    async Task ToggleAsync()
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
            return;
        }

        _connection = new ChatConnection(_server.Text, ChatEndpoint.Port);
        _stop = new();
        SetConnected(true);

        try
        {
            // Sin ConfigureAwait: cada mensaje vuelve al hilo de UI.
            await foreach (var m in _connection.Chat.Join(_room.Text, _user.Text, _stop.Token))
                _messages.TopIndex = _messages.Items.Add($"{m.User}: {m.Text}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _messages.Items.Add($"(disconnected: {ex.Message})");
            await DisconnectAsync();
        }
    }

    async Task SendAsync()
    {
        if (_connection is null || _input.Text.Length == 0) return;

        var text = _input.Text;
        _input.Clear();

        try { await _connection.Chat.PostAsync(new(_room.Text, _user.Text, text)); }
        catch (Exception ex) { _messages.Items.Add($"(not sent: {ex.Message})"); }
    }

    async Task DisconnectAsync()
    {
        var (connection, stop) = (_connection, _stop);
        (_connection, _stop) = (null, null);
        SetConnected(false);

        stop?.Cancel();
        stop?.Dispose();
        if (connection is not null) await connection.DisposeAsync();
    }

    void SetConnected(bool connected)
    {
        _connect.Text = connected ? "Disconnect" : "Connect";
        _server.Enabled = _user.Enabled = _room.Enabled = !connected;
        _input.Enabled = _send.Enabled = connected;
        if (connected) _input.Focus();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        await DisconnectAsync();
    }
}

[ServiceClient(ServiceConnectionType.Tcp)]
[ServiceUnit<IChat>]
[ServiceProvider]
sealed partial class ChatConnection;
