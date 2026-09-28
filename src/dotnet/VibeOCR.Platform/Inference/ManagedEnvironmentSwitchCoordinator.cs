using System.Security.Cryptography;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.Platform.Inference;

public sealed class ManagedEnvironmentSession(
    string environmentId,
    int revision,
    InferenceSupervisorProcess process,
    InferenceHttpClient client,
    QrCodeHttpClient qrClient,
    RuntimeStatusSnapshot status) : IAsyncDisposable
{
    public string EnvironmentId { get; } = environmentId;
    public int Revision { get; } = revision;
    public InferenceSupervisorProcess Process { get; } = process;
    public InferenceHttpClient Client { get; } = client;
    public QrCodeHttpClient QrClient { get; } = qrClient;
    public RuntimeStatusSnapshot Status { get; } = status;

    public async ValueTask DisposeAsync()
    {
        Process.Dispose();
        await Client.DisposeAsync().ConfigureAwait(false);
        await QrClient.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Starts and checks a target Supervisor before committing the Runtime-owned
/// active pointer. The caller publishes the new session before the old process
/// is disposed. Failed startup or CAS leaves the old session untouched.
/// </summary>
public sealed class ManagedEnvironmentSwitchCoordinator(IManagedEnvironmentClient manager)
{
    internal static IReadOnlyList<string> RuntimeArguments(RuntimeLaunch launch) =>
        launch.Environment.ContainsKey("VIBEOCR_PRODUCT_CODE_ROOT")
            ? ["-I", "-B", "-c", "import os,runpy,sys;sys.path.insert(0,os.environ['VIBEOCR_PRODUCT_CODE_ROOT']);runpy.run_module('vibeocr.runtime.host.main',run_name='__main__')"]
            : ["-m", launch.SupervisorModule];

    public async Task<ManagedEnvironmentSession?> SwitchAsync(
        string environmentId,
        ManagedEnvironmentSession? current,
        Action<ManagedEnvironmentSession?> publish,
        string logPath,
        TimeSpan startupTimeout,
        IReadOnlySet<string> requiredCapabilities,
        CancellationToken cancellationToken = default,
        bool injectSoakCrash = false)
    {
        ArgumentNullException.ThrowIfNull(publish);
        PreparedEnvironmentSwitch prepared = await manager.PrepareEnvironmentSwitchAsync(
            environmentId, cancellationToken).ConfigureAwait(false);
        ManagedEnvironmentSession? candidate = null;
        StartedEnvironmentHealth? evidence = null;
        bool committed = false;
        try
        {
            if (prepared.RequiresSupervisor)
            {
                RuntimeLaunch launch = prepared.Launch
                    ?? throw new InvalidDataException("Runtime did not provide a launch for the installed environment.");
                if (!string.Equals(launch.PythonExecutable, prepared.Python, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Runtime launch Python differs from the prepared revision.");
                string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var environment = launch.Environment.ToDictionary(
                    item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
                if (injectSoakCrash)
                    environment["VIBEOCR_SUPERVISOR_SOAK_CRASH_AFTER_READY"] = "1";
                var options = new InferenceSupervisorOptions(
                    launch.PythonExecutable,
                    RuntimeArguments(launch),
                    launch.WorkingDirectory,
                    logPath,
                    startupTimeout,
                    requiredCapabilities,
                    environment);
                var process = new InferenceSupervisorProcess(options, token);
                InferenceHttpClient? client = null;
                QrCodeHttpClient? qrClient = null;
                try
                {
                    SupervisorReadyEnvelope ready = await process.StartAsync(cancellationToken)
                        .ConfigureAwait(false);
                    client = new InferenceHttpClient(ready.BaseUrl, token);
                    qrClient = new QrCodeHttpClient(ready.BaseUrl, token);
                    var health = await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
                    RuntimeStatusSnapshot status = await client.GetRuntimeStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!health.Ready || health.Draining || health.InstanceId != ready.InstanceId ||
                        status.InstanceId != ready.InstanceId || status.ServiceState != RuntimeServiceState.Ready)
                        throw new InvalidDataException("Target Supervisor has not passed Runtime health checks.");
                    evidence = new StartedEnvironmentHealth(ready.Port, ready.InstanceId);
                    candidate = new ManagedEnvironmentSession(
                        prepared.EnvironmentId, prepared.EnvironmentRevision, process, client, qrClient, status);
                    client = null;
                    qrClient = null;
                }
                catch
                {
                    process.Dispose();
                    if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                    if (qrClient is not null) await qrClient.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            else if (prepared.Launch is not null)
            {
                throw new InvalidDataException("Empty environment must not launch a Supervisor.");
            }

            await manager.CommitEnvironmentSwitchAsync(
                prepared, evidence, cancellationToken)
                .ConfigureAwait(false);
            committed = true;
            publish(candidate);
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
            return candidate;
        }
        catch
        {
            if (!committed && candidate is not null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
