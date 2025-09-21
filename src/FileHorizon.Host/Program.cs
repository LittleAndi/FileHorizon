using FileHorizon.Application;
using FileHorizon.Application.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationServices();
builder.Services.Configure<PollingOptions>(builder.Configuration.GetSection(PollingOptions.SectionName));
builder.Services.Configure<FileSourcesOptions>(builder.Configuration.GetSection(FileSourcesOptions.SectionName));
builder.Services.Configure<PipelineFeaturesOptions>(builder.Configuration.GetSection(PipelineFeaturesOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));
// Bind pipeline role options
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
