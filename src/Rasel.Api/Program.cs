using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Rasel.Api;
using Rasel.Api.Endpoints;
using Rasel.Api.Security;
using Rasel.Application;
using Rasel.Infrastructure;
using Rasel.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddOptions<ApiOptions>().Bind(config.GetSection("Api")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
builder.Services.AddRaselApplication(config);
builder.Services.AddRaselInfrastructure(config);

builder.Services.AddOptions<HostOptions>().Configure<IOptions<ApiOptions>>((host, api) =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(api.Value.ShutdownTimeoutSeconds));
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    kestrel.Limits.MaxRequestBodySize = context.Configuration.GetValue("Api:MaxRequestBodyBytes", new ApiOptions().MaxRequestBodyBytes));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();

// Fail closed: every endpoint requires a valid API key unless it is explicitly marked AllowAnonymous.
builder.Services.AddAuthentication(ApiKeyDefaults.Scheme).AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder()
    .AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser().Build());

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc(ApiVersions.V1, new OpenApiInfo { Title = "Rasel", Version = ApiVersions.V1, Description = "WhatsApp messaging and OTP gateway." });
        o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = ApiKeyDefaults.HeaderName, In = ParameterLocation.Header });
        o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] });
    });
}

var app = builder.Build();

await app.Services.InitializeRaselAsync();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ClientRateLimitMiddleware>();

app.MapRaselHealth();

var v1 = app.MapVersion(ApiVersions.V1);
v1.MapSessionEndpoints();
v1.MapMessageEndpoints();
v1.MapOtpEndpoints();

app.Run();

public partial class Program;
