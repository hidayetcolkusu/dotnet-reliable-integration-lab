using System.Net;
using System.Net.Sockets;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// A loopback TCP endpoint that can be switched between "nothing is listening" and "forwarding
/// to the real dependency".
///
/// This is how a SQL Server outage is produced without touching the container: the worker is
/// configured once, at startup, to reach SQL through this port, so closing the gate is a real
/// connection failure for every new connection the worker opens - and opening it again restores
/// the same address the worker is already configured for. Stopping the shared container instead
/// would take the database away from every other test in the collection.
///
/// The port is reserved for the lifetime of the gate (a closed listener is kept bound while
/// shut), so nothing else can take it during the outage.
/// </summary>
public sealed class LoopbackTcpGate : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _forwardHost;
    private readonly int _forwardPort;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TcpClient> _connections = [];
    private readonly Lock _gate = new();

    private Task? _accepting;
    private bool _open;

    private LoopbackTcpGate(TcpListener listener, string forwardHost, int forwardPort)
    {
        _listener = listener;
        _forwardHost = forwardHost;
        _forwardPort = forwardPort;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Creates the gate CLOSED: the port is reserved but refuses every connection.</summary>
    public static LoopbackTcpGate CreateClosed(string forwardHost, int forwardPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Stop, then re-create bound to the SAME port. Between Open() calls the socket is not
        // accepting, so a connection attempt fails immediately instead of hanging - which is the
        // failure shape a database outage actually has for a client with a connect timeout.
        listener.Stop();
        var reserved = new TcpListener(IPAddress.Loopback, port);
        return new LoopbackTcpGate(reserved, forwardHost, forwardPort);
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_open)
            {
                return;
            }

            _listener.Start();
            _open = true;
        }

        _accepting = Task.Run(() => AcceptAsync(_stopping.Token));
    }

    /// <summary>Closes the gate and cuts every connection already established through it.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _listener.Stop();
            _open = false;

            foreach (var connection in _connections)
            {
                try
                {
                    connection.Close();
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }

            _connections.Clear();
        }
    }

    private async Task AcceptAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(stopping);
            }
            catch (Exception)
            {
                // Stopped, or the gate was closed underneath us.
                return;
            }

            lock (_gate)
            {
                if (!_open)
                {
                    inbound.Close();
                    continue;
                }

                _connections.Add(inbound);
            }

            _ = Task.Run(() => PumpAsync(inbound, stopping), CancellationToken.None);
        }
    }

    private async Task PumpAsync(TcpClient inbound, CancellationToken stopping)
    {
        TcpClient? outbound = null;
        try
        {
            outbound = new TcpClient();
            await outbound.ConnectAsync(_forwardHost, _forwardPort, stopping);

            lock (_gate)
            {
                _connections.Add(outbound);
            }

            var inboundStream = inbound.GetStream();
            var outboundStream = outbound.GetStream();
            await Task.WhenAny(
                inboundStream.CopyToAsync(outboundStream, stopping),
                outboundStream.CopyToAsync(inboundStream, stopping));
        }
        catch (Exception)
        {
            // A cut connection is the whole point of this class.
        }
        finally
        {
            outbound?.Dispose();
            inbound.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        Close();

        if (_accepting is not null)
        {
            try
            {
                await _accepting;
            }
            catch (Exception)
            {
                // Shutdown races are not interesting.
            }
        }

        _stopping.Dispose();
    }
}
