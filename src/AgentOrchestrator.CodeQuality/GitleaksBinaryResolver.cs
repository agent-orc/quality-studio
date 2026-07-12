using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Formats.Tar;
using System.Runtime.InteropServices;

namespace AgentOrchestrator.CodeQuality;

public sealed class GitleaksBinaryResolver
{
    public const string PinnedVersion = "8.24.2";

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly string _downloadRoot;

    public GitleaksBinaryResolver(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _cacheDirectory = cacheDirectory ?? GetDefaultCacheDirectory();
        _downloadRoot = $"https://github.com/gitleaks/gitleaks/releases/download/v{PinnedVersion}";
    }

    public async Task<string> ResolveAsync(string? explicitPath = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return await VerifyBinaryAsync(explicitPath, cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidate in EnumeratePathCandidates())
        {
            if (File.Exists(candidate) && await IsPinnedVersionAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        var cached = GetInstalledBinaryPath();
        if (File.Exists(cached) && await IsPinnedVersionAsync(cached, cancellationToken).ConfigureAwait(false))
        {
            return cached;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        await DownloadPinnedBinaryAsync(cached, cancellationToken).ConfigureAwait(false);
        return cached;
    }

    private async Task<string> VerifyBinaryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new SecurityScannerUnavailableException($"Gitleaks binary was not found at '{path}'.");
        }

        if (!await IsPinnedVersionAsync(path, cancellationToken).ConfigureAwait(false))
        {
            throw new SecurityScannerUnavailableException(
                $"Gitleaks at '{path}' does not report pinned version {PinnedVersion}.");
        }

        return path;
    }

    private async Task<bool> IsPinnedVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunVersionAsync(executable, cancellationToken).ConfigureAwait(false);
            return string.Equals(result.Trim(), $"v{PinnedVersion}", StringComparison.Ordinal) ||
                   string.Equals(result.Trim(), PinnedVersion, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> RunVersionAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("version");

        try
        {
            if (!process.Start())
            {
                throw new SecurityScannerUnavailableException("Gitleaks version check did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new SecurityScannerUnavailableException("Gitleaks could not be executed.", exception);
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new SecurityScannerUnavailableException(
                $"Gitleaks version check failed: {error.Trim()}".Trim());
        }

        return output.Trim();
    }

    private async Task DownloadPinnedBinaryAsync(string destination, CancellationToken cancellationToken)
    {
        var archiveName = GetArchiveName();
        var checksums = await DownloadTextAsync($"{_downloadRoot}/{PinnedVersionAssetChecksumsName()}", cancellationToken)
            .ConfigureAwait(false);
        var expectedDigest = ParseChecksum(checksums, archiveName);

        var archivePath = Path.Combine(Path.GetTempPath(), $"gitleaks-{PinnedVersion}-{Guid.NewGuid():N}{GetArchiveExtension()}");
        await DownloadFileAsync($"{_downloadRoot}/{archiveName}", archivePath, cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyChecksum(archivePath, expectedDigest);
            ExtractArchive(archivePath, destination);
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    private static string PinnedVersionAssetChecksumsName() => $"gitleaks_{PinnedVersion}_checksums.txt";

    private string GetArchiveName()
    {
        var (platform, architecture, extension) = GetArchiveCoordinates();
        return $"gitleaks_{PinnedVersion}_{platform}_{architecture}{extension}";
    }

    private static string GetArchiveExtension() =>
        OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    private static (string Platform, string Architecture, string Extension) GetArchiveCoordinates()
    {
        var platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "darwin"
                    : throw new PlatformNotSupportedException("Unsupported operating system for Gitleaks provisioning.");

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Unsupported architecture for Gitleaks provisioning."),
        };

        return (platform, architecture, GetArchiveExtension());
    }

    private static string GetDefaultCacheDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        return Path.Combine(root, "QualityStudio", "gitleaks");
    }

    private string GetInstalledBinaryPath()
    {
        var (platform, architecture, _) = GetArchiveCoordinates();
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return Path.Combine(_cacheDirectory, PinnedVersion, $"{platform}-{architecture}", $"gitleaks{extension}");
    }

    private static IEnumerable<string> EnumeratePathCandidates()
    {
        var executable = OperatingSystem.IsWindows() ? "gitleaks.exe" : "gitleaks";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(segment, executable);
        }
    }

    private async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SecurityScannerUnavailableException($"Failed to download Gitleaks asset: {url}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SecurityScannerUnavailableException($"Failed to download Gitleaks checksum file: {url}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ParseChecksum(string checksums, string assetName)
    {
        foreach (var line in checksums.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(assetName, StringComparison.Ordinal))
            {
                continue;
            }

            var digest = line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .FirstOrDefault(token => token.Length == 64 && token.All(char.IsAsciiHexDigit));
            if (digest is not null)
            {
                return digest.ToLowerInvariant();
            }
        }

        throw new SecurityScannerUnavailableException($"Could not find checksum for Gitleaks asset '{assetName}'.");
    }

    private static void VerifyChecksum(string archivePath, string expectedDigest)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archivePath)));
        if (!string.Equals(actual, expectedDigest, StringComparison.Ordinal))
        {
            throw new SecurityScannerUnavailableException("Downloaded Gitleaks archive checksum did not match the pinned checksum.");
        }
    }

    private static void ExtractArchive(string archivePath, string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"gitleaks-unzip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            try
            {
                ZipFile.ExtractToDirectory(archivePath, tempDirectory, true);
                var extracted = Directory.EnumerateFiles(tempDirectory, OperatingSystem.IsWindows() ? "gitleaks.exe" : "gitleaks", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? throw new SecurityScannerUnavailableException("Gitleaks archive did not contain an executable.");
                File.Copy(extracted, destination, true);
            }
            finally
            {
                TryDelete(tempDirectory);
            }
        }
        else
        {
            var tarPath = Path.Combine(Path.GetTempPath(), $"gitleaks-{Guid.NewGuid():N}.tar");
            try
            {
                using var archive = File.OpenRead(archivePath);
                using var gzip = new GZipStream(archive, CompressionMode.Decompress);
                using var tar = File.Create(tarPath);
                gzip.CopyTo(tar);
                tar.Close();

                TarFile.ExtractToDirectory(tarPath, directory, overwriteFiles: true);
                var extracted = Directory.EnumerateFiles(directory, "gitleaks", SearchOption.AllDirectories)
                    .FirstOrDefault() ?? throw new SecurityScannerUnavailableException("Gitleaks archive did not contain an executable.");
                if (!string.Equals(extracted, destination, StringComparison.Ordinal))
                {
                    File.Copy(extracted, destination, true);
                }
            }
            finally
            {
                TryDelete(tarPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
