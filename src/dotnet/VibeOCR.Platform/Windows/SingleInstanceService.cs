using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VibeOCR.Platform.Windows;

public sealed class SingleInstanceService : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>, Task> _argumentHandler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Mutex _marker;
    private readonly string _pipeName;
    private readonly Thread? _listener;

    public SingleInstanceService(
        string instanceName,
        Func<IReadOnlyList<string>, Task> argumentHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        _argumentHandler = argumentHandler ?? throw new ArgumentNullException(nameof(argumentHandler));
        string normalized = new(instanceName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (normalized.Length is 0 or > 120)
        {
            throw new ArgumentException("Instance name must contain 1–120 safe characters.", nameof(instanceName));
        }

        _pipeName = $"{normalized}-activation";
        _marker = new Mutex(false, $@"Local\{normalized}", out bool createdNew);
        NamedPipeServerStream? pendingServer = null;
        if (createdNew)
        {
            // Create the listening pipe before mutex ownership becomes
            // observable so a secondary launch can always connect without
            // waiting for listener startup.
            pendingServer = CreateServer(_pipeName);
        }

        IsPrimary = createdNew;
        if (pendingServer is not null)
        {
            // A dedicated thread keeps activation delivery independent of
            // thread-pool scheduling; pool starvation must not delay or
            // silently drop forwarded arguments.
            _listener = new Thread(() => Listen(pendingServer))
            {
                IsBackground = true,
                Name = $"{normalized}-activation-listener",
            };
            _listener.Start();
        }
    }

    public bool IsPrimary { get; }

    public async Task ForwardAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (IsPrimary)
        {
            throw new InvalidOperationException("The primary instance cannot forward to itself.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (payload.Length > 64 * 1024)
        {
            throw new ArgumentException("Forwarded arguments exceed 64 KiB.", nameof(arguments));
        }

        Exception? lastError = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                lastError = error;
            }
        }

        throw new IOException("Primary instance did not accept forwarded arguments.", lastError);
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.In,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.CurrentUserOnly,
        64 * 1024,
        64 * 1024);

    private void Listen(NamedPipeServerStream firstServer)
    {
        NamedPipeServerStream? prepared = firstServer;
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = prepared ?? CreateServer(_pipeName);
                prepared = null;
                server.WaitForConnection();
                using var buffer = new MemoryStream();
                server.CopyTo(buffer);
                if (buffer.Length <= 64 * 1024)
                {
                    string[] arguments = JsonSerializer.Deserialize<string[]>(buffer.ToArray()) ?? [];
                    _argumentHandler(arguments).GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
                // A malformed or aborted activation must not disable future
                // forwarding; cancellation exits via the loop condition.
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_listener is not null)
        {
            Knock();
            if (_listener.Join(TimeSpan.FromSeconds(5)))
            {
                _shutdown.Dispose();
            }
        }
        else
        {
            _shutdown.Dispose();
        }

        _marker.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Knock()
    {
        // Wake a listener blocked in WaitForConnection so it can observe the
        // shutdown flag; the empty connection is discarded by Listen.
        try
        {
            using var knock = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            knock.Connect(500);
        }
        catch (Exception)
        {
            // The listener was not waiting; the shutdown flag ends the loop.
        }
    }
}
