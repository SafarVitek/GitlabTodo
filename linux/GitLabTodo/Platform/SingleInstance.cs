using System.Net.Sockets;
using System.Text;

namespace GitLabTodo.Platform;

/// One running app per user. Starting it again, e.g. from the app menu while it sits in the tray, asks the running one
/// over a Unix socket to show its window and exits.
public static class SingleInstance
{
    private static Socket? listener;

    private static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(runtime)
                ? Path.Combine(Path.GetTempPath(), $"gitlab-todo-{Environment.UserName}.sock")
                : Path.Combine(runtime, "gitlab-todo.sock");
        }
    }

    /// True when another instance took the message.
    public static bool NotifyRunning()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes("show\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// Calls `onShow` on a background thread whenever another start asks for the window.
    public static void Listen(Action onShow)
    {
        try
        {
            // Nobody answered, so a socket file left here belongs to an instance that crashed.
            File.Delete(SocketPath);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(SocketPath));
            socket.Listen(4);
            listener = socket;
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Druhé spuštění nepůjde předat: {exception.Message}");
            return;
        }
        var thread = new Thread(() =>
        {
            while (listener is { } socket)
            {
                try
                {
                    using var connection = socket.Accept();
                    var buffer = new byte[16];
                    if (connection.Receive(buffer) > 0)
                    {
                        onShow();
                    }
                }
                catch (SocketException)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    public static void Stop()
    {
        listener?.Dispose();
        listener = null;
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
        }
    }
}
