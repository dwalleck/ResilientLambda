using Amazon.SimpleNotificationService;
using Amazon.Extensions.NETCore.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Extensions.Hosting;
using OpenTelemetry.Contrib.Instrumentation.AWS;
using OpenTelemetry.Instrumentation.Runtime;
using OpenTelemetry.Exporter;
using Polly;
using System;
using System.Collections.Generic;
using System.IO;

namespace ResilientLambda;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Configuration setup
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddConsole();

            // Add your company's internal logging provider here
            // builder.AddYourCompanyLoggingProvider(...)
        });

        // Register AWS services
        services.AddAWSService<IAmazonSimpleNotificationService>();

        // Register data access services
        services.AddScoped<IDataRepository, DataRepository>();

        // Register data transformation services
        services.AddScoped<IDataTransformer, DataTransformer>();

        // Register SNS publisher with resilience
        services.AddSingleton<IResilientSnsPublisher, ResilientSnsPublisher>(sp =>
        {
            var snsClient = sp.GetRequiredService<IAmazonSimpleNotificationService>();
            var logger = sp.GetRequiredService<ILogger<ResilientSnsPublisher>>();
            var topicArn = configuration["AWS:SNS:TopicArn"];

            return new ResilientSnsPublisher(snsClient, logger, topicArn);
        });

        // Register main data processor
        services.AddScoped<IDataProcessor, DataProcessor>();

        // Register telemetry
        ConfigureOpenTelemetry(services, configuration);
    }

    private static void ConfigureOpenTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        var serviceName = configuration["ServiceName"] ?? "DataProcessingService";
        var serviceVersion = configuration["ServiceVersion"] ?? "1.0.0";

        // Define resource attributes
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: serviceVersion)
            .AddTelemetrySdk()
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = configuration["Environment"] ?? "Production"
            });

        // Configure OpenTelemetry
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddSource(TelemetryConstants.ServiceName)
                .AddAWSInstrumentation()
                .AddOtlpExporter()
            )
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(TelemetryConstants.ServiceName)
                .AddRuntimeInstrumentation()
                .AddOtlpExporter()
            );
    }
}
