using System.Text;
using Tmds.DBus.Protocol;

namespace GitLabTodo.Core;

/// The desktop's keyring over D-Bus: the Secret Service API of GNOME Keyring, KWallet or KeePassXC. One item per
/// GitLab account, found by its ID.
public sealed class SecretService : IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    /// D-Bus's null object path: no collection, or nothing to ask the user.
    private const string NoPath = "/";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    /// How long the user has to type the keyring's password.
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    private delegate void BodyWriter(ref MessageWriter writer);

    private delegate T BodyReader<T>(ref Reader reader);

    private readonly Connection connection;
    private string session = NoPath;

    private SecretService(Connection connection) => this.connection = connection;

    /// Null where the desktop has no keyring, or no session bus at all.
    public static async Task<SecretService?> Connect()
    {
        if (Address.Session is not { } address)
        {
            return null;
        }
        var service = new SecretService(new Connection(address));
        // Any failure just means there is no keyring to use.
        try
        {
            await service.connection.ConnectAsync().AsTask().WaitAsync(CallTimeout);
            if (!(await service.connection.ListServicesAsync()).Contains(Service)
                && !(await service.connection.ListActivatableServicesAsync()).Contains(Service))
            {
                service.Dispose();
                return null;
            }
            // Plain: the session bus only connects this user's own processes, which can read an open keyring anyway.
            service.session = await service.Call(ServicePath, ServiceInterface, "OpenSession", "sv",
                (ref MessageWriter w) =>
                {
                    w.WriteString("plain");
                    w.WriteVariantString("");
                },
                (ref Reader r) =>
                {
                    r.ReadVariantValue();
                    return r.ReadObjectPathAsString();
                });
            return service;
        }
        catch (Exception)
        {
            service.Dispose();
            return null;
        }
    }

    /// The account's token, or null where the keyring has none.
    public async Task<string?> Get(Guid account)
    {
        var (unlocked, locked) = await Search(account);
        if (unlocked.Length == 0 && locked.Length > 0)
        {
            await Unlock(locked);
            unlocked = locked;
        }
        if (unlocked.Length == 0)
        {
            return null;
        }
        var token = await Call(unlocked[0], ItemInterface, "GetSecret", "o",
            (ref MessageWriter w) => w.WriteObjectPath(session),
            (ref Reader r) =>
            {
                r.AlignStruct();
                r.ReadObjectPath();
                r.ReadArrayOfByte();
                return r.ReadArrayOfByte();
            });
        return Encoding.UTF8.GetString(token);
    }

    /// Stores the token in the default keyring, replacing the account's previous one.
    public async Task Set(Guid account, string label, string token)
    {
        var collection = await Call(ServicePath, ServiceInterface, "ReadAlias", "s",
            (ref MessageWriter w) => w.WriteString("default"),
            (ref Reader r) => r.ReadObjectPathAsString());
        if (collection == NoPath)
        {
            throw new SecretServiceException("Klíčenka nemá výchozí úložiště");
        }
        await Unlock([collection]);
        var prompt = await Call(collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b",
            (ref MessageWriter w) =>
            {
                var properties = w.WriteDictionaryStart();
                w.WriteDictionaryEntryStart();
                w.WriteString("org.freedesktop.Secret.Item.Label");
                w.WriteVariantString(label);
                w.WriteDictionaryEntryStart();
                w.WriteString("org.freedesktop.Secret.Item.Attributes");
                w.WriteSignature("a{ss}");
                WriteAttributes(ref w, account);
                w.WriteDictionaryEnd(properties);
                w.WriteStructureStart();
                w.WriteObjectPath(session);
                w.WriteArray(Array.Empty<byte>());
                w.WriteArray(Encoding.UTF8.GetBytes(token));
                w.WriteString("text/plain");
                w.WriteBool(true);
            },
            (ref Reader r) =>
            {
                r.ReadObjectPath();
                return r.ReadObjectPathAsString();
            });
        await Prompt(prompt);
    }

    public async Task Delete(Guid account)
    {
        var (unlocked, locked) = await Search(account);
        if (locked.Length > 0)
        {
            await Unlock(locked);
        }
        foreach (var item in unlocked.Concat(locked))
        {
            await Prompt(await Call(item, ItemInterface, "Delete", null, null, (ref Reader r) => r.ReadObjectPathAsString()));
        }
    }

    public void Dispose() => connection.Dispose();

    private Task<(string[] Unlocked, string[] Locked)> Search(Guid account) =>
        Call(ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter w) => WriteAttributes(ref w, account),
            (ref Reader r) => (Paths(r.ReadArrayOfObjectPath()), Paths(r.ReadArrayOfObjectPath())));

    private async Task Unlock(string[] paths)
    {
        var prompt = await Call(ServicePath, ServiceInterface, "Unlock", "ao",
            (ref MessageWriter w) => w.WriteArray(paths.Select(p => new ObjectPath(p)).ToArray()),
            (ref Reader r) =>
            {
                r.ReadArrayOfObjectPath();
                return r.ReadObjectPathAsString();
            });
        await Prompt(prompt);
    }

    /// Lets the keyring ask the user, e.g. for the password of a locked keyring.
    private async Task Prompt(string prompt)
    {
        if (prompt == NoPath)
        {
            return;
        }
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule { Type = MessageType.Signal, Path = prompt, Interface = PromptInterface, Member = "Completed" };
        using var match = await connection.AddMatchAsync<bool>(rule,
            (message, _) => message.GetBodyReader().ReadBool(),
            (exception, dismissed, _, _) =>
            {
                if (exception is not null)
                {
                    completed.TrySetException(exception);
                }
                else
                {
                    completed.TrySetResult(dismissed);
                }
            },
            null, null, emitOnCapturedContext: false, ObserverFlags.None);
        await Call(prompt, PromptInterface, "Prompt", "s", (ref MessageWriter w) => w.WriteString(""), (ref Reader _) => true);
        if (await completed.Task.WaitAsync(PromptTimeout))
        {
            throw new SecretServiceException("Klíčenka zůstala zamčená");
        }
    }

    private Task<T> Call<T>(string path, string @interface, string member, string? signature, BodyWriter? write, BodyReader<T> read)
    {
        var writer = connection.GetMessageWriter();
        MessageBuffer call;
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: member, signature: signature);
            write?.Invoke(ref writer);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return connection.CallMethodAsync(call, (message, _) =>
        {
            var reader = message.GetBodyReader();
            return read(ref reader);
        }).WaitAsync(CallTimeout);
    }

    private static void WriteAttributes(ref MessageWriter writer, Guid account)
    {
        var attributes = writer.WriteDictionaryStart();
        foreach (var (key, value) in new[] { ("xdg:schema", "local.gitlabtodo.Token"), ("account", account.IdString()) })
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteString(value);
        }
        writer.WriteDictionaryEnd(attributes);
    }

    private static string[] Paths(ObjectPath[] paths) => paths.Select(p => p.ToString()).ToArray();
}

public sealed class SecretServiceException(string message) : Exception(message);
