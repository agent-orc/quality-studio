using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>Produces a stable identity for every setting that belongs to a registry entry.</summary>
public static class RepositoryCacheState
{
    public static string RegistrationFingerprint(RepositoryRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var canonical = new
        {
            schema = 1,
            registration.Id,
            registration.DisplayName,
            rootPath = Path.GetFullPath(registration.RootPath),
            globalInputsDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
                ? null
                : Path.GetFullPath(registration.GlobalInputsDirectory),
            registration.InputBudgetCharacters,
            enabledReviewKinds = registration.EnabledReviewKinds.Order(StringComparer.Ordinal).ToArray(),
            sensors = (registration.Sensors ?? [])
                .OrderBy(sensor => sensor.Id, StringComparer.Ordinal)
                .Select(sensor => new
                {
                    sensor.Id,
                    sensor.Enabled,
                    configuration = sensor.Configuration?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new[] { pair.Key, pair.Value }).ToArray(),
                }).ToArray(),
            registration.Archived,
            registration.DefaultReviewTokenCap,
            registration.DefaultReviewCostCap,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string CombinedKey(string repositoryState, RepositoryRegistration registration)
    {
        var value = repositoryState + "\0" + RegistrationFingerprint(registration);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
