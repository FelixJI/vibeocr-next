using System.Diagnostics;
using System.Text.Json;

namespace VibeOCR.Platform.Bootstrap;

/// <summary>A single Runtime stdin session owns the existing target lock until commit or abort.</summary>
internal sealed class EnvironmentSwitchReservation : IManagedEnvironmentSwitchReservation
{
    private readonly Process process;
    private readonly Task<string> errors;
    private bool terminal;
    public PreparedEnvironmentSwitch Prepared { get; private set; } = null!;

    private EnvironmentSwitchReservation(Process process)
    {
        this.process = process;
        errors = process.StandardError.ReadToEndAsync();
    }

    public static async Task<IManagedEnvironmentSwitchReservation> StartAsync(
        ProcessStartInfo startInfo, string environmentId, CancellationToken cancellationToken)
    {
        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new RuntimeInstallerException("无法启动环境切换 reservation。");
        var session = new EnvironmentSwitchReservation(process);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            session.Prepared = await session.ReadAsync<PreparedEnvironmentSwitch>("prepare_switch", deadline.Token).ConfigureAwait(false);
            if (session.Prepared.EnvironmentId != environmentId || session.Prepared.EnvironmentRevision < 1 || session.Prepared.ActiveRevision < 0)
                throw new RuntimeInstallerException("环境切换 reservation 与请求不匹配。");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> ReadAsync<T>(string action, CancellationToken cancellationToken)
    {
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) throw new RuntimeInstallerException("环境切换 reservation 提前退出。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement envelope = document.RootElement;
            if (envelope.TryGetProperty("error", out JsonElement error))
                throw new RuntimeInstallerException(error.GetProperty("message").GetString() ?? "环境切换失败。");
            if (envelope.GetProperty("protocol_version").GetInt32() != 2 ||
                envelope.GetProperty("response_kind").GetString() != "environment" ||
                envelope.GetProperty("action").GetString() != action)
                throw new RuntimeInstallerException("环境切换 reservation 响应无效。");
            return envelope.GetProperty("result").Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new RuntimeInstallerException("环境切换 reservation 响应为空。");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new RuntimeInstallerException($"环境切换 reservation 响应无效：{exception.Message}");
        }
    }

    public async Task<CommittedEnvironmentSwitch> CommitAsync(StartedEnvironmentHealth? health, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal) throw new InvalidOperationException("环境切换 reservation 已结束。");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { action = "commit_switch", started_health = health })).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        // Once commit was sent, observe its actual CAS result before honoring a late cancellation.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CommittedEnvironmentSwitch committed = await ReadAsync<CommittedEnvironmentSwitch>("commit_switch", deadline.Token).ConfigureAwait(false);
        if (committed.ActiveId != Prepared.EnvironmentId || committed.ActiveRevision < Prepared.ActiveRevision)
            throw new RuntimeInstallerException("环境切换提交结果与 reservation 不匹配。");
        terminal = true;
        return committed;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                if (!terminal)
                {
                    await process.StandardInput.WriteLineAsync("{\"action\":\"abort_switch\"}").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            }
            await errors.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
        }
        finally { process.Dispose(); }
    }
}
