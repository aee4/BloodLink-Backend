using BloodLink.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);
await AwsSecretConfiguration.AddConfiguredSecretsAsync(builder.Configuration);
builder.AddBloodLinkApi();

var app = builder.Build();
await app.ConfigureBloodLinkApiAsync();
app.Run();

public partial class Program { }
