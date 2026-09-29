using System.Security.Cryptography;
using System.Text;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using BloodLink.Api.Security;
using BloodLink.Application.Interfaces;
using BloodLink.Infrastructure;
using BloodLink.Infrastructure.Data;
using BloodLink.Infrastructure.Data.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

namespace BloodLink.Api.Configuration;

public static class BloodLinkApiStartup
{
    public static void AddBloodLinkApi(this WebApplicationBuilder builder)
    {
        var isDevelopment = builder.Environment.IsDevelopment();

        if (isDevelopment && string.IsNullOrWhiteSpace(builder.Configuration[$"{ApiTokenOptions.SectionName}:SigningKey"]))
            builder.Configuration[$"{ApiTokenOptions.SectionName}:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

        var signingKey = builder.Configuration[$"{ApiTokenOptions.SectionName}:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException("Configure Api:Tokens:SigningKey with at least 32 bytes using user secrets or a secret manager.");
        builder.Services.Configure<ApiTokenOptions>(options =>
        {
            builder.Configuration.GetSection(ApiTokenOptions.SectionName).Bind(options);
            options.SigningKey = signingKey;
        });
        var tokenLifetimeMinutes = builder.Configuration.GetValue<int?>($"{ApiTokenOptions.SectionName}:LifetimeMinutes") ?? 15;
        var refreshLifetimeDays = builder.Configuration.GetValue<int?>($"{ApiTokenOptions.SectionName}:RefreshTokenLifetimeDays") ?? 14;
        if (tokenLifetimeMinutes is < 1 or > 60 || refreshLifetimeDays is < 1 or > 90)
            throw new InvalidOperationException("Configure access-token lifetime from 1 to 60 minutes and refresh-token lifetime from 1 to 90 days.");

        var origins = builder.Configuration.GetSection("Api:AllowedOrigins").Get<string[]>() ?? [];
        if (isDevelopment && origins.Length == 0)
            origins = ["https://localhost:7081", "http://localhost:5081"];
        if (origins.Length == 0 || origins.Any(origin => origin == "*" || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (!isDevelopment && uri.Scheme != Uri.UriSchemeHttps)))
            throw new InvalidOperationException("Configure Api:AllowedOrigins with explicit frontend origins; production origins must use HTTPS.");

        builder.Services.AddBloodLinkInfrastructure(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.RemoveAll<ICurrentUserService>();
        builder.Services.AddScoped<ICurrentUserService, ApiCurrentUserService>();
        builder.Services.AddControllers();
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            var defaultFactory = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
            {
                if (context.HttpContext.Request.Path.Equals("/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase)
                    && context.HttpContext.Request.Method == HttpMethods.Post)
                {
                    return new UnauthorizedObjectResult(new ProblemDetails
                    {
                        Status = StatusCodes.Status401Unauthorized,
                        Title = "Authentication required",
                        Detail = "The refresh session is invalid or expired.",
                        Extensions =
                        {
                            ["traceId"] = context.HttpContext.TraceIdentifier,
                            ["code"] = "invalid_refresh"
                        }
                    });
                }

                return defaultFactory(context);
            };
        });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "BloodLink API", Version = "v1" });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Short-lived Identity-backed access token."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
            });
            var xml = Path.Combine(AppContext.BaseDirectory, "BloodLink.Api.xml");
            if (File.Exists(xml)) options.IncludeXmlComments(xml);
        });
        builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT")
            .WithHeaders("Authorization", "Content-Type")));
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = true;
                options.RequireHttpsMetadata = !isDevelopment;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = builder.Configuration[$"{ApiTokenOptions.SectionName}:Issuer"] ?? "BloodLink.Api",
                    ValidateAudience = true,
                    ValidAudience = builder.Configuration[$"{ApiTokenOptions.SectionName}:Audience"] ?? "BloodLink.Frontend",
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = System.Security.Claims.ClaimTypes.Name,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddHealthChecks();
        builder.Services.AddHealthChecks().AddCheck<DatabaseReadyHealthCheck>("database-ready", tags: ["ready"]);
        builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
    }

    public static async Task ConfigureBloodLinkApiAsync(this WebApplication app)
    {
        app.UseMiddleware<ApiSecurityHeadersMiddleware>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseCors("Frontend");
        app.UseAuthentication();
        app.UseAuthorization();

        var openApiEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:OpenApi:Enabled");
        if (openApiEnabled)
        {
            app.UseSwagger();
            if (app.Environment.IsDevelopment()) app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "BloodLink API v1"));
        }

        var initializeDatabase = app.Configuration.GetValue<bool>("BloodLink:DatabaseInitialization:Enabled");
        if (app.Environment.IsDevelopment() && initializeDatabase)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BloodLinkDbContext>();
            await db.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }
        else if (!app.Environment.IsDevelopment() && initializeDatabase)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        }

        app.MapControllers();
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });
    }
}
