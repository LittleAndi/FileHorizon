using FileHorizon.Application;
using FileHorizon.Application.Configuration;

using FileHorizon.Application.Infrastructure.Orchestration; // for hosted service

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.SectionName));
builder.Services.AddHostedService<FilePipelineBackgroundService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
