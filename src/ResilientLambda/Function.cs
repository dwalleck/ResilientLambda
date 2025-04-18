using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ResilientLambda;

public class Function
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Function> _logger;

    public Function()
    {
        // Configure DI container
        var services = new ServiceCollection();
        Startup.ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<Function>>();
    }

    // Constructor for testing
    public Function(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = _serviceProvider.GetRequiredService<ILogger<Function>>();
    }

    public async Task FunctionHandler(object input, ILambdaContext context)
    {
        _logger.LogInformation("DataProcessingService Lambda function invoked at {Time}", DateTime.UtcNow);

        try
        {
            // Resolve the data processor and run the processing pipeline
            var dataProcessor = _serviceProvider.GetRequiredService<IDataProcessor>();
            await dataProcessor.ProcessAndPublishDataAsync();

            _logger.LogInformation("Data processing completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during data processing");
            throw; // Rethrow to signal Lambda failure
        }
    }
}
