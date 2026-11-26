using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FanMaxxxing.Core;

/// <summary>
/// Una sola instancia por sesión. Si se abre otra (p. ej. desde el acceso directo "Enfriar y apagar"),
/// le pasa sus argumentos a la que ya está abierta por un named pipe y se cierra.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FanMaxxxing.Instance.v1";
    private static readonly string PipeName = "FanMaxxxing.Pipe." + CurrentUserSid();

    private Mutex? _mutex;
    private bool _owned;
    private CancellationTokenSource? _cts;
    private Task? _server;

    private static string CurrentUserSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? "anon";
        }
        catch { return "anon"; }
    }

    /// <summary>¿Hay otra instancia abierta? (sin adquirir nada)</summary>
    public static bool AnotherInstanceExists()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var m))
            {
                m.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // existe, pero es de una instancia elevada
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Intenta ser la instancia principal; si se indica, reintenta durante <paramref name="wait"/>.</summary>
    public bool TryAcquire(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out bool createdNew);
                _owned = createdNew;
                if (createdNew) return true;
                _mutex.Dispose();
                _mutex = null;
            }
            catch (UnauthorizedAccessException)
            {
                // existe y es de otra instancia elevada
            }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(200);
        }
    }

    public static bool SendToExisting(IEnumerable<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Append("--show")));
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo contactar con la instancia abierta: " + ex.Message);
            return false;
        }
    }

    public void StartServer(Action<string[]> onArgs)
    {
        if (_server != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _server = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = CreateServer();
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    using var ms = new MemoryStream();
                    int read;
                    while (ms.Length < 16 * 1024 &&
                           (read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }
                    var args = Encoding.UTF8.GetString(ms.ToArray())
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    onArgs(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.ErrorThrottled("Error en el pipe de instancia única", ex);
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }, ct);
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        using (var id = WindowsIdentity.GetCurrent())
        {
            if (id.User != null)
                security.AddAccessRule(new PipeAccessRule(id.User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignorar */ }
        try { _server?.Wait(1500); } catch { /* ignorar */ }
        _cts?.Dispose();
        _cts = null;
        _server = null;
        if (_mutex != null)
        {
            try { if (_owned) _mutex.ReleaseMutex(); } catch { /* otro hilo: el SO lo libera al salir */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
