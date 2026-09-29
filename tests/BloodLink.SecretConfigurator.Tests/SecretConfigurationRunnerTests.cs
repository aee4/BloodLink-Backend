using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BloodLink.SecretConfigurator.Tests;

public sealed class SecretConfigurationRunnerTests
{
    private const string RdsPassword = "Rds-Password-That-Should-Not-Leak-819";
    private const string BootstrapPassword = "ValidAdmin9";

    [Fact]
    public async Task Valid_password_is_accepted_and_three_allowed_secrets_are_stored()
    {
        var store = new CaptureSecretStore();

        var result = await SecretConfigurationRunner.ConfigureAsync(ValidInput(), store);

        Assert.Equal(3, store.Calls.Count);
        Assert.Equal([
            SecretConfigurationRunner.DatabaseSecretName,
            SecretConfigurationRunner.AuthenticationSecretName,
            SecretConfigurationRunner.BootstrapSecretName
        ], store.Calls.Select(call => call.Name).ToArray());
        Assert.Contains(SecretConfigurationRunner.DatabaseSecretName, result.SecretNames);
    }

    [Theory]
    [InlineData("A1b")]
    [InlineData("lowercase1")]
    [InlineData("UPPERCASE1")]
    [InlineData("NoDigitsHere")]
    public async Task Invalid_password_is_rejected_before_secret_store_call(string password)
    {
        var store = new CaptureSecretStore();

        await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput(password), store));

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Too_short_password_is_rejected()
    {
        var store = new CaptureSecretStore();

        var exception = await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput("A1b"), store));

        Assert.Contains("at least 8", exception.Message);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Missing_uppercase_password_is_rejected()
    {
        var store = new CaptureSecretStore();

        var exception = await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput("lowercase1"), store));

        Assert.Contains("uppercase", exception.Message);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Missing_lowercase_password_is_rejected()
    {
        var store = new CaptureSecretStore();

        var exception = await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput("UPPERCASE1"), store));

        Assert.Contains("lowercase", exception.Message);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Missing_digit_password_is_rejected()
    {
        var store = new CaptureSecretStore();

        var exception = await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput("NoDigitsHere"), store));

        Assert.Contains("digit", exception.Message);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Secret_values_never_appear_in_success_or_validation_output()
    {
        var successStore = new CaptureSecretStore();
        var success = await SecretConfigurationRunner.ConfigureAsync(ValidInput(), successStore);
        var successOutput = success.Message + Environment.NewLine + string.Join(Environment.NewLine, success.SecretNames);

        Assert.DoesNotContain(RdsPassword, successOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapPassword, successOutput, StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<SecretValidationException>(() =>
            SecretConfigurationRunner.ConfigureAsync(ValidInput("NoDigitsHere"), new CaptureSecretStore()));
        Assert.DoesNotContain(RdsPassword, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NoDigitsHere", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_helper_accepts_standard_input_and_does_not_print_secret_values()
    {
        var tempStore = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"bloodlink-secret-test-{Guid.NewGuid():N}"));
        try
        {
            var result = await RunHelperAsync(ValidInput(), tempStore.FullName);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Secrets created or updated by name only", result.Output);
            Assert.DoesNotContain(RdsPassword, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(BootstrapPassword, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(RdsPassword, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(BootstrapPassword, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            tempStore.Delete(true);
        }
    }

    [Fact]
    public async Task Console_helper_does_not_store_any_secret_when_validation_fails()
    {
        var tempStore = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"bloodlink-secret-test-{Guid.NewGuid():N}"));
        try
        {
            var result = await RunHelperAsync(ValidInput("NoDigitsHere"), tempStore.FullName);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(tempStore.GetFiles());
            Assert.DoesNotContain(RdsPassword, result.Output + result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("NoDigitsHere", result.Output + result.Error, StringComparison.Ordinal);
        }
        finally
        {
            tempStore.Delete(true);
        }
    }

    [Fact]
    public void PowerShell_helper_removes_temporary_payload_and_clears_plaintext_in_finally()
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "configure-aws-production-secrets.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("finally {", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-Item -LiteralPath $payloadPath -Force", script, StringComparison.Ordinal);
        Assert.Contains("ZeroFreeBSTR($bstrRds)", script, StringComparison.Ordinal);
        Assert.Contains("ZeroFreeBSTR($bstrBootstrap)", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Variable plainRdsPassword, plainBootstrapPassword, payload", script, StringComparison.Ordinal);
    }

    private static SecretConfigurationInput ValidInput(string bootstrapPassword = BootstrapPassword) =>
        new(
            "eu-north-1",
            "bloodlink-db.example.rds.amazonaws.com",
            RdsPassword,
            "admin@example.test",
            bootstrapPassword,
            "System",
            "Admin");

    private static async Task<ProcessResult> RunHelperAsync(SecretConfigurationInput input, string storeDirectory)
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "src", "BloodLink.SecretConfigurator", "BloodLink.SecretConfigurator.csproj");
        var startInfo = new ProcessStartInfo("dotnet", $"run --project \"{projectPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["BLOODLINK_SECRET_CONFIGURATOR_STORE_DIR"] = storeDirectory;
        startInfo.Environment["BLOODLINK_SECRET_CONFIGURATOR_TESTING"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start helper process.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, SerializerOptions.Options));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BloodLink.Backend.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate BloodLink.Backend.sln.");
    }

    private sealed class CaptureSecretStore : ISecretStore
    {
        public List<(string Name, string Value)> Calls { get; } = [];

        public Task UpsertBloodLinkSecretAsync(string name, string value, CancellationToken cancellationToken)
        {
            Calls.Add((name, value));
            return Task.CompletedTask;
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
