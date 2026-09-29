using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace BloodLink.Api.Configuration;

public static class AwsSecretConfiguration
{
    public const string SecretNamesKey = "BloodLink:AwsSecrets:Names";

    public static async Task AddConfiguredSecretsAsync(IConfigurationManager configuration, CancellationToken cancellationToken = default)
    {
        var names = configuration[SecretNamesKey];
        if (string.IsNullOrWhiteSpace(names))
        {
            return;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var client = new AmazonSecretsManagerClient();
        foreach (var secretName in names.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretName }, cancellationToken);
            if (string.IsNullOrWhiteSpace(response.SecretString))
            {
                continue;
            }

            using var document = JsonDocument.Parse(response.SecretString);
            Flatten(document.RootElement, null, values);
        }

        configuration.AddInMemoryCollection(values);
    }

    private static void Flatten(JsonElement element, string? prefix, IDictionary<string, string?> values)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                values[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }

            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix) ? property.Name.Replace("__", ":", StringComparison.Ordinal) : $"{prefix}:{property.Name}";
            Flatten(property.Value, key, values);
        }
    }
}
