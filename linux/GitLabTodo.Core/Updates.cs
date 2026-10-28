using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace GitLabTodo.Core;

/// `latest.json` of the newest GitHub release, written by the release workflow: the version and, per platform, the
/// file to download and its signature.
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("platforms")]
    public Dictionary<string, UpdateFile> Platforms { get; set; } = [];
}

/// `signature`: ECDSA P-256 over SHA-256 of the file, DER, Base64; made with OpenSSL from the release key.
public sealed record UpdateFile(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("signature")] string Signature);

/// A newer release for this platform.
public sealed record AvailableUpdate(string Version, string? Notes, string Url, byte[] Signature);

public static class Updates
{
    public const string ManifestUrl = "https://github.com/SafarVitek/GitlabTodo/releases/latest/download/latest.json";

    /// The public half of the release key; the private half signs releases in GitHub Actions. An update that does not
    /// verify against it is never installed.
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDZuEm7Zr1KJRamjcKx6hVyAEj83hhBVnLMZqPlirMi6au7gW23LgXnNLaCEyDsMLX2ImVpqDn6QNlZlxVe2HLw==";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// The key of this platform in the manifest, null where there are no builds that update themselves.
    public static string? Platform => OperatingSystem.IsLinux()
        ? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x86_64",
            Architecture.Arm64 => "linux-aarch64",
            _ => null,
        }
        : null;

    /// `0.2.0` → `0.10.0` counts as newer; a suffix like `-beta.1` is ignored, the release workflow strips it too.
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string version) => version.Split('-', '+')[0].Split('.')
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0)
            .Concat([0, 0, 0]).Take(3).ToArray();
        var (a, b) = (Parts(candidate), Parts(current));
        for (var index = 0; index < 3; index++)
        {
            if (a[index] != b[index])
            {
                return a[index] > b[index];
            }
        }
        return false;
    }

    /// The update the manifest offers this platform, if it is newer than what runs.
    public static AvailableUpdate? Pick(UpdateManifest manifest, string current, string platform)
    {
        if (!IsNewer(manifest.Version, current) || !manifest.Platforms.TryGetValue(platform, out var file))
        {
            return null;
        }
        try
        {
            return new AvailableUpdate(manifest.Version, manifest.Notes, file.Url, Convert.FromBase64String(file.Signature));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static async Task<AvailableUpdate?> Check(string current, string platform)
    {
        var manifest = await Http.GetFromJsonAsync<UpdateManifest>(ManifestUrl);
        return manifest is null ? null : Pick(manifest, current, platform);
    }

    public static bool Verify(Stream content, byte[] signature, string publicKey = PublicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(content, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// Downloads the update to `path` and checks its signature; a file that fails the check is deleted.
    public static async Task Download(AvailableUpdate update, string path, IProgress<double>? progress = null)
    {
        using (var response = await Http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }
        bool isValid;
        await using (var written = File.OpenRead(path))
        {
            isValid = Verify(written, update.Signature);
        }
        if (!isValid)
        {
            File.Delete(path);
            throw new InvalidDataException("Stažená aktualizace nemá platný podpis vydání GitLab Todo, nic se neinstalovalo.");
        }
    }
}
