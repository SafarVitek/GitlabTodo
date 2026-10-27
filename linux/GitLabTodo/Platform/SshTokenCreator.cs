using System.Diagnostics;

namespace GitLabTodo.Platform;

/// Creates a token with GitLab Shell's `personal_access_token` command, authenticated by the user's SSH key.
public static class SshTokenCreator
{
    public static async Task<string> Create(string target, int days = 365)
    {
        var start = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // BatchMode: fail instead of waiting for a password prompt nobody can answer.
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", target, "personal_access_token", "gitlab-todo", "api", days.ToString() })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("SSH nejde spustit");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var token = (await output).Split('\n')
            .FirstOrDefault(line => line.StartsWith("Token:", StringComparison.Ordinal))?["Token:".Length..].Trim();
        if (!string.IsNullOrEmpty(token))
        {
            return token;
        }
        var detail = (await errors).Trim();
        throw new InvalidOperationException(detail.Length == 0 ? "SSH nevrátilo token" : detail);
    }
}
