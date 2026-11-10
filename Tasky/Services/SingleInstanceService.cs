using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Tasky.Services;

/// <summary>
/// Ensures only one Tasky process runs. A second launch signals the running
/// instance (so it can bring its window forward) and then exits quietly.
/// Windows signals through a named event; macOS, which has no named events,
/// through a local pipe.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexBaseName         = "Tasky_SingleInstance_Mutex_v1";
    private const string ActivateEventBaseName = "Tasky_SingleInstance_Activate_v1";

    // A sandboxed copy (TASKY_DATA_ROOT set) is a separate "profile" and may run alongside the real one.
    // Workspaces are not: one Tasky runs at a time, whichever workspace it has open.
    private static string Scope => Environment.GetEnvironmentVariable(AppPaths.DataRootOverrideVariable) is { Length: > 0 }
        ? "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
              System.Text.Encoding.UTF8.GetBytes(AppPaths.DefaultDataFolder.ToLowerInvariant())))[..16]
        : string.Empty;

    private static string MutexName         => MutexBaseName + Scope;
    private static string ActivateEventName => ActivateEventBaseName + Scope;

    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly IActivationChannel _activation;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        _activation = OperatingSystem.IsWindows()
            ? new EventActivationChannel(ActivateEventName)
            : new PipeActivationChannel(ActivateEventName);
    }

    /// <summary>True when this process is the first (primary) instance.</summary>
    public bool IsPrimaryInstance => _ownsMutex;

    /// <summary>Asks the already-running instance to show its main window.</summary>
    public void SignalPrimaryInstance() => _activation.Signal();

    /// <summary>
    /// Invokes <paramref name="onActivateRequested"/> (on a background thread)
    /// every time a later launch calls <see cref="SignalPrimaryInstance"/>.
    /// </summary>
    public void ListenForActivation(Action onActivateRequested) => _activation.Listen(onActivateRequested);

    public void Dispose()
    {
        _activation.Dispose();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    // ── Activation channels ───────────────────────────────────────────────

    private interface IActivationChannel : IDisposable
    {
        void Signal();
        void Listen(Action onActivateRequested);
    }

    [SupportedOSPlatform("windows")]
    private sealed class EventActivationChannel(string name) : IActivationChannel
    {
        private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset, name);
        private RegisteredWaitHandle? _registration;

        public void Signal()
        {
            // This launch holds foreground rights (the user just started it);
            // hand them over so the primary instance may bring itself to front.
            Platform.Windows.NativeMethods.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            _event.Set();
        }

        public void Listen(Action onActivateRequested)
            => _registration = ThreadPool.RegisterWaitForSingleObject(
                   _event, (_, _) => onActivateRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

        public void Dispose()
        {
            _registration?.Unregister(null);
            _event.Dispose();
        }
    }

    private sealed class PipeActivationChannel(string name) : IActivationChannel
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

        // A Unix socket path may hold only 104 bytes, so use a short absolute path instead of
        // the default $TMPDIR/CoreFxPipe_<name> (which .NET would build from the long name).
        private readonly string _path = "/tmp/tasky-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12];
        private readonly CancellationTokenSource _stop = new();

        public void Signal()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _path, PipeDirection.Out);
                client.Connect(ConnectTimeout);
                client.WriteByte(1);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                // The other copy is closing (or the channel is unavailable) — nothing to bring forward.
            }
        }

        public void Listen(Action onActivateRequested) => _ = Task.Run(async () =>
        {
            // Only the instance holding the mutex listens, so a socket file left here is from a crash.
            try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_path, PipeDirection.In, 1);
                    await server.WaitForConnectionAsync(_stop.Token);
                    onActivateRequested();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    CrashLog.Write("Single-instance pipe error", ex);
                    if (ex is not IOException) return;   // not recoverable by retrying
                }
            }
        });

        public void Dispose() => _stop.Cancel();
    }
}
