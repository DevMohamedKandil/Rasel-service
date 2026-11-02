using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Rasel.Api;
using Rasel.Api.Endpoints;
using Rasel.Application;
using Rasel.Application.Common;
using Rasel.Infrastructure;
using Rasel.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddRaselApplication(config.GetSection("Otp").Get<OtpOptions>() ?? new OtpOptions());
builder.Services.AddRaselInfrastructure(config);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Rasel", Version = "v1", Description = "WhatsApp messaging and OTP gateway (WhatsApp is FAKE in this prototype)." });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = ApiKeyMiddleware.HeaderName, In = ParameterLocation.Header });
    o.AddSecurityRequirement(doc => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] });
});

var app = builder.Build();

await app.Services.InitializeRaselAsync();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

var v1 = app.MapGroup("/v1");
v1.MapSessionEndpoints();
v1.MapMessageEndpoints();
v1.MapOtpEndpoints();

app.Run();

public partial class Program;
