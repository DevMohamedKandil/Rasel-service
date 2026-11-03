using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Rasel.Api;
using Rasel.Api.Endpoints;
using Rasel.Api.Security;
using Rasel.Application;
using Rasel.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(); // no-op when not run by the Service Control Manager; sets the content root to the exe folder when it is
var config = builder.Configuration;

builder.Services.AddOptions<ApiOptions>().Bind(config.GetSection("Api")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
builder.Services.AddOptions<CorsOptions>().Bind(config.GetSection("Cors")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CorsOptions>, CorsOptionsValidator>();
var corsOrigins = (config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).ToArray();
if (corsOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).WithHeaders(ApiKeyDefaults.HeaderName, "Content-Type", "Idempotency-Key").WithMethods("GET", "POST", "DELETE")));
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
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser().Build();
    o.AddRaselPolicies();
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc(ApiVersions.V1, new OpenApiInfo { Title = "Rasel", Version = ApiVersions.V1, Description = "WhatsApp messaging and OTP gateway. Powered by Kandil Smart Digital Solution." });
        o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = ApiKeyDefaults.HeaderName, In = ParameterLocation.Header });
        o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] });
    });
}

if (config.GetValue<bool>("Api:TrustForwardedHeaders"))
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1; // only the hop added by our own proxy
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

var app = builder.Build();
if (config.GetValue<bool>("Api:TrustForwardedHeaders")) app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (corsOrigins.Length > 0) app.UseCors(); // before auth so preflight (no API key) is answered
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ClientRateLimitMiddleware>();

app.MapRaselHealth();

var v1 = app.MapVersion(ApiVersions.V1);
v1.MapSessionEndpoints();
v1.MapMessageEndpoints();
v1.MapOtpEndpoints();
v1.MapSystemEndpoints();
v1.MapKeyEndpoints();

app.Run();

public partial class Program;
