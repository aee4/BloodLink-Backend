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
    public void Production_accepts_the_exact_cloudfront_origin()
    {
        var builder = CreateProductionBuilder("https://d2z1pcfp95dfwd.cloudfront.net");

        builder.AddBloodLinkApi();
    }

    [Fact]
    public void Production_template_uses_one_exact_origin_for_gateway_and_lambda()
    {
        var root = FindRepositoryRoot();
        var template = File.ReadAllText(Path.Combine(root, "template.yaml"));
        var startup = File.ReadAllText(Path.Combine(root, "src", "BloodLink.Api", "Configuration", "BloodLinkApiStartup.cs"));

        Assert.Contains("Default: https://d2z1pcfp95dfwd.cloudfront.net", template, StringComparison.Ordinal);
        Assert.Contains("Api__AllowedOrigins__0: !Ref CorsOrigin", template, StringComparison.Ordinal);
        Assert.Matches("AllowOrigins:\\s+- !Ref CorsOrigin", template);
        Assert.Contains("AllowedPattern: '^https://[A-Za-z0-9.-]+(?::[0-9]{1,5})?$'", template, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder.invalid", template, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnyOrigin", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_rejects_invalid_token_lifetime_configuration()
    {
        var builder = CreateProductionBuilder();
        builder.Configuration["Api:Tokens:LifetimeMinutes"] = "0";

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddBloodLinkApi());

        Assert.Contains("access-token lifetime", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationBuilder CreateProductionBuilder(string origin = "https://d2z1pcfp95dfwd.cloudfront.net")
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
