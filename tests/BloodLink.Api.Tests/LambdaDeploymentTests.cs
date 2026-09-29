using BloodLink.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BloodLink.Api.Tests;

public sealed class LambdaDeploymentTests
{
    [Fact]
    public void Api_registration_keeps_local_hosting_services_and_adds_lambda_hosting()
    {
        var builder = CreateProductionBuilder();

        builder.AddBloodLinkApi();

        using var provider = builder.Services.BuildServiceProvider();
        Assert.NotNull(provider);
        var projectFile = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "BloodLink.Api", "BloodLink.Api.csproj"));
        Assert.Contains("Amazon.Lambda.AspNetCoreServer.Hosting", projectFile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://frontend.invalid")]
    public void Production_rejects_wildcard_and_non_https_cors_origins(string origin)
    {
        var builder = CreateProductionBuilder(origin);

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddBloodLinkApi());

        Assert.Contains("Api:AllowedOrigins", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_rejects_invalid_token_lifetime_configuration()
    {
        var builder = CreateProductionBuilder();
        builder.Configuration["Api:Tokens:LifetimeMinutes"] = "0";

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddBloodLinkApi());

        Assert.Contains("access-token lifetime", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationBuilder CreateProductionBuilder(string origin = "https://placeholder.invalid")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Server=localhost;Database=BloodLink;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True",
            ["Api:Tokens:SigningKey"] = "ApiTestKeyAtLeastThirtyTwoCharactersLongForHmacSigning!",
            ["Api:AllowedOrigins:0"] = origin,
            ["BloodLink:DatabaseInitialization:Enabled"] = "false",
            ["BloodLink:FacilityRegistration:AutoApproveInDevelopment"] = "false",
            ["BloodLink:BootstrapAdmin:Enabled"] = "false"
        });
        return builder;
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
}
