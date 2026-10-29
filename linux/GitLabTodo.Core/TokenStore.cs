namespace GitLabTodo.Core;

/// Access tokens in the desktop's keyring, or in `tokens.json` readable only by the user where there is none or it
/// fails. Tokens left in the file move to the keyring once it works.
public sealed class TokenStore(string path, bool usesKeyring = true)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// The tokens found, and why some may be missing.
    public sealed record Loaded(Dictionary<Guid, string> Tokens, string? Problem);

    public async Task<Loaded> Load(IReadOnlyList<GitLabAccount> accounts)
    {
        await gate.WaitAsync();
        try
        {
            var tokens = new Dictionary<Guid, string>();
            string? problem = null;
            // Only a failed keyring write leaves a token in the file, so it is the newer one.
            var file = ReadFile();
            using var keyring = await Keyring();
            if (keyring is not null)
            {
                try
                {
                    foreach (var account in accounts.Where(a => !file.ContainsKey(a.Id)))
                    {
                        if (await keyring.Get(account.Id) is { } token)
                        {
                            tokens[account.Id] = token;
                        }
                    }
                    var moving = accounts.Where(a => file.ContainsKey(a.Id)).ToList();
                    foreach (var account in moving)
                    {
                        await keyring.Set(account.Id, Label(account), file[account.Id]);
                    }
                    if (file.Count > 0)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Nepovedlo se smazat {path}: {exception.Message}");
                }
                catch (Exception exception)
                {
                    problem = exception is SecretServiceException
                        ? $"{exception.Message}, token z ní nejde načíst"
                        : $"Token nejde načíst z klíčenky: {exception.Message}";
                }
            }
            foreach (var account in accounts.Where(a => file.ContainsKey(a.Id)))
            {
                tokens[account.Id] = file[account.Id];
            }
            return new Loaded(tokens, problem);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Save(GitLabAccount account, string token)
    {
        await gate.WaitAsync();
        try
        {
            using var keyring = await Keyring();
            try
            {
                if (keyring is not null)
                {
                    await keyring.Set(account.Id, Label(account), token);
                    RemoveFromFile(account.Id);
                    return;
                }
            }
            catch (Exception exception) when (exception is not IOException and not UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Token nejde uložit do klíčenky, ukládám ho do {path}: {exception.Message}");
            }
            var file = ReadFile();
            file[account.Id] = token;
            WriteFile(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nepovedlo se uložit {path}: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task Delete(Guid account)
    {
        await gate.WaitAsync();
        try
        {
            using var keyring = await Keyring();
            try
            {
                if (keyring is not null)
                {
                    await keyring.Delete(account);
                }
            }
            catch (Exception exception) when (exception is not IOException and not UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Token nejde smazat z klíčenky: {exception.Message}");
            }
            RemoveFromFile(account);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Nepovedlo se uložit {path}: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SecretService?> Keyring() => usesKeyring ? await SecretService.Connect() : null;

    /// What the keyring app lists it as.
    private static string Label(GitLabAccount account) => $"GitLab Todo: {account.Name}";

    private Dictionary<Guid, string> ReadFile() =>
        (Storage.Load<Dictionary<string, string>>(path) ?? [])
        .Where(t => Guid.TryParse(t.Key, out _))
        .ToDictionary(t => Guid.Parse(t.Key), t => t.Value);

    private void WriteFile(Dictionary<Guid, string> tokens) =>
        Storage.Save(path, tokens.ToDictionary(t => t.Key.IdString(), t => t.Value), isPrivate: true);

    private void RemoveFromFile(Guid account)
    {
        var file = ReadFile();
        if (!file.Remove(account))
        {
            return;
        }
        if (file.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            WriteFile(file);
        }
    }
}
