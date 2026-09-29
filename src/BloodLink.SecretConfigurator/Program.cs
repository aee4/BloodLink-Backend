using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace BloodLink.SecretConfigurator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--disable-bootstrap"])
        {
            return await DisableBootstrapAsync();
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Unsupported command.");
            return 2;
        }

        try
        {
            var inputJson = await Console.In.ReadToEndAsync();
            var input = JsonSerializer.Deserialize<SecretConfigurationInput>(inputJson, SerializerOptions.Options)
                ?? throw new InvalidOperationException("Input was empty or invalid JSON.");
            var store = CreateSecretStore(input.Region);
            var result = await SecretConfigurationRunner.ConfigureAsync(input, store);
            Console.Out.WriteLine(result.Message);
            foreach (var name in result.SecretNames)
            {
                Console.Out.WriteLine($" - {name}");
            }

            return 0;
        }
        catch (SecretValidationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Secret configuration failed. No secret values were printed.");
            return 1;
        }
    }

    private static async Task<int> DisableBootstrapAsync()
    {
        string? updatedSecret = null;
        try
        {
            using var client = new AmazonSecretsManagerClient(RegionEndpoint.EUNorth1);
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = SecretConfigurationRunner.BootstrapSecretName
            });
            if (string.IsNullOrWhiteSpace(response.SecretString))
            {
                throw new InvalidOperationException();
            }

            var secret = JsonNode.Parse(response.SecretString);
            var bootstrap = secret?["bloodLink"]?["bootstrapAdmin"];
            if (bootstrap is null)
            {
                throw new InvalidOperationException();
            }

            if (bootstrap["enabled"]?.GetValue<bool>() == false)
            {
                Console.Out.WriteLine("Bootstrap was already disabled in bloodlink/prod/bootstrap.");
                return 0;
            }

            bootstrap["enabled"] = false;
            updatedSecret = secret!.ToJsonString(SerializerOptions.Options);
            await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = SecretConfigurationRunner.BootstrapSecretName,
                SecretString = updatedSecret
            });

            Console.Out.WriteLine("Bootstrap disabled in bloodlink/prod/bootstrap.");
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Could not disable bootstrap. No secret values were printed.");
            return 1;
        }
        finally
        {
            updatedSecret = string.Empty;
        }
    }

    private static ISecretStore CreateSecretStore(string region)
    {
        var testStoreDirectory = Environment.GetEnvironmentVariable("BLOODLINK_SECRET_CONFIGURATOR_STORE_DIR");
        var testMode = Environment.GetEnvironmentVariable("BLOODLINK_SECRET_CONFIGURATOR_TESTING");
        if (!string.IsNullOrWhiteSpace(testStoreDirectory) && testMode != "1")
        {
            throw new InvalidOperationException("The file secret store is available only in test mode.");
        }

        return string.IsNullOrWhiteSpace(testStoreDirectory)
            ? new AwsSecretStore(region)
            : new FileSecretStore(testStoreDirectory);
    }
}

public static class SecretConfigurationRunner
{
    public const string DatabaseSecretName = "bloodlink/prod/database";
    public const string AuthenticationSecretName = "bloodlink/prod/authentication";
    public const string BootstrapSecretName = "bloodlink/prod/bootstrap";

    public static async Task<SecretConfigurationResult> ConfigureAsync(
        SecretConfigurationInput input,
        ISecretStore secretStore,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);

        var signingKeyBytes = new byte[64];
        RandomNumberGenerator.Fill(signingKeyBytes);
        var signingKey = Convert.ToBase64String(signingKeyBytes);

        var connectionString =
            $"Server={input.RdsEndpoint},1433;Database=BloodLink;User Id=bloodlinkadmin;Password={input.RdsPassword};Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True";
        var databaseSecret = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new
            {
                DefaultConnection = connectionString
            }
        }, SerializerOptions.Options);
        var authenticationSecret = JsonSerializer.Serialize(new
        {
            Api = new
            {
                Tokens = new
                {
                    SigningKey = signingKey,
                    LifetimeMinutes = 15,
                    RefreshTokenLifetimeDays = 14
                }
            }
        }, SerializerOptions.Options);
        var bootstrapSecret = JsonSerializer.Serialize(new
        {
            BloodLink = new
            {
                BootstrapAdmin = new
                {
                    Enabled = true,
                    Email = input.BootstrapEmail,
                    Password = input.BootstrapPassword,
                    FirstName = input.BootstrapFirstName,
                    LastName = input.BootstrapLastName
                }
            }
        }, SerializerOptions.Options);

        try
        {
            await secretStore.UpsertBloodLinkSecretAsync(DatabaseSecretName, databaseSecret, cancellationToken);
            await secretStore.UpsertBloodLinkSecretAsync(AuthenticationSecretName, authenticationSecret, cancellationToken);
            await secretStore.UpsertBloodLinkSecretAsync(BootstrapSecretName, bootstrapSecret, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKeyBytes);
            signingKey = string.Empty;
            connectionString = string.Empty;
            databaseSecret = string.Empty;
            authenticationSecret = string.Empty;
            bootstrapSecret = string.Empty;
        }

        return new SecretConfigurationResult("Secrets created or updated by name only:", [
            DatabaseSecretName,
            AuthenticationSecretName,
            BootstrapSecretName
        ]);
    }

    private static void ValidateInput(SecretConfigurationInput input)
    {
        Require(input.Region, "AWS region is required.");
        Require(input.RdsEndpoint, "RDS endpoint is required.");
        Require(input.RdsPassword, "RDS password is required.");
        Require(input.BootstrapEmail, "Bootstrap email is required.");
        Require(input.BootstrapPassword, "Bootstrap password is required.");
        Require(input.BootstrapFirstName, "Bootstrap first name is required.");
        Require(input.BootstrapLastName, "Bootstrap last name is required.");

        var password = input.BootstrapPassword;
        if (password.Length < 8)
        {
            throw new SecretValidationException("Bootstrap password must be at least 8 characters.");
        }

        if (!password.Any(char.IsUpper))
        {
            throw new SecretValidationException("Bootstrap password must include an uppercase letter.");
        }

        if (!password.Any(char.IsLower))
        {
            throw new SecretValidationException("Bootstrap password must include a lowercase letter.");
        }

        if (!password.Any(char.IsDigit))
        {
            throw new SecretValidationException("Bootstrap password must include a digit.");
        }

        string[] prohibited = ["password", "password123", "changeme", "admin123", "temporary123!"];
        if (prohibited.Contains(password, StringComparer.OrdinalIgnoreCase))
        {
            throw new SecretValidationException("Bootstrap password is a known placeholder and cannot be used.");
        }
    }

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretValidationException(message);
        }
    }
}

public sealed class AwsSecretStore(string region) : ISecretStore
{
    private static readonly Dictionary<string, string> RequiredTags = new(StringComparer.Ordinal)
    {
        ["Project"] = "BloodLink",
        ["Environment"] = "SchoolDemo",
        ["ManagedBy"] = "Codex",
        ["Owner"] = "aee4"
    };

    public async Task UpsertBloodLinkSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        if (!IsAllowedSecretName(name))
        {
            throw new InvalidOperationException("Refusing to manage an unexpected secret name.");
        }

        using var client = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
        try
        {
            var existing = await client.DescribeSecretAsync(new DescribeSecretRequest { SecretId = name }, cancellationToken);
            var isBloodLinkSecret = existing.Tags.Any(tag => tag.Key == "Project" && tag.Value == "BloodLink")
                || existing.Tags.Any(tag => tag.Key == "ManagedBy" && tag.Value == "Codex");
            if (!isBloodLinkSecret)
            {
                throw new InvalidOperationException($"Refusing to overwrite existing secret '{name}' because it is not tagged as BloodLink/Codex-managed.");
            }

            await client.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = name,
                SecretString = value
            }, cancellationToken);
        }
        catch (ResourceNotFoundException)
        {
            await client.CreateSecretAsync(new CreateSecretRequest
            {
                Name = name,
                SecretString = value,
                Tags = RequiredTags.Select(pair => new Tag { Key = pair.Key, Value = pair.Value }).ToList()
            }, cancellationToken);
        }
    }

    private static bool IsAllowedSecretName(string name) => name is
        SecretConfigurationRunner.DatabaseSecretName or
        SecretConfigurationRunner.AuthenticationSecretName or
        SecretConfigurationRunner.BootstrapSecretName;
}

public interface ISecretStore
{
    Task UpsertBloodLinkSecretAsync(string name, string value, CancellationToken cancellationToken);
}

public sealed class FileSecretStore(string directory) : ISecretStore
{
    public async Task UpsertBloodLinkSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var fileName = name.Replace('/', '_');
        await File.WriteAllTextAsync(Path.Combine(directory, $"{fileName}.json"), value, cancellationToken);
    }
}

public sealed record SecretConfigurationInput(
    string Region,
    string RdsEndpoint,
    string RdsPassword,
    string BootstrapEmail,
    string BootstrapPassword,
    string BootstrapFirstName,
    string BootstrapLastName);

public sealed record SecretConfigurationResult(string Message, IReadOnlyList<string> SecretNames);

public sealed class SecretValidationException(string message) : Exception(message);

public static class SerializerOptions
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
