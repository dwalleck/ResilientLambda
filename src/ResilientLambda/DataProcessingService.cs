using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ResilientLambda;

public interface IDataProcessor
{
    Task ProcessAndPublishDataAsync();
}

public class DataProcessor : IDataProcessor
{
    private readonly IDataRepository _dataRepository;
    private readonly IDataTransformer _dataTransformer;
    private readonly IResilientSnsPublisher _snsPublisher;
    private readonly ILogger<DataProcessor> _logger;

    public DataProcessor(
        IDataRepository dataRepository,
        IDataTransformer dataTransformer,
        IResilientSnsPublisher snsPublisher,
        ILogger<DataProcessor> logger)
    {
        _dataRepository = dataRepository ?? throw new ArgumentNullException(nameof(dataRepository));
        _dataTransformer = dataTransformer ?? throw new ArgumentNullException(nameof(dataTransformer));
        _snsPublisher = snsPublisher ?? throw new ArgumentNullException(nameof(snsPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProcessAndPublishDataAsync()
    {
        using var activity = TelemetryManager.StartActivity("ProcessAndPublishData");
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Starting data processing and publication");

        try
        {
            // 1. Retrieve data from database
            _logger.LogInformation("Retrieving data from database");
            var data = await _dataRepository.GetDataAsync();

            TelemetryManager.RecordDataItemsRetrieved(data.Count);
            _logger.LogInformation("Retrieved {Count} data items from database", data.Count);

            // 2. Transform data
            _logger.LogInformation("Transforming data");
            var transformedMessages = _dataTransformer.TransformData(data);

            TelemetryManager.RecordDataItemsTransformed(transformedMessages.Count);
            _logger.LogInformation("Transformed data into {Count} messages", transformedMessages.Count);

            // 3. Publish data using channel pattern for controlled concurrency
            await PublishMessagesUsingChannelAsync(transformedMessages);

            // 4. Record total processing time
            stopwatch.Stop();
            TelemetryManager.RecordTotalProcessingTime(stopwatch.ElapsedMilliseconds);

            _logger.LogInformation("Completed data processing and publication in {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during data processing");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task PublishMessagesUsingChannelAsync(List<string> messages)
    {
        // Create metrics for monitoring
        var successCount = 0;
        var failureCount = 0;
        var channelBackpressureTime = 0L;

        // Create bounded channel for backpressure
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        // Start multiple consumer tasks
        var consumerCount = Math.Min(20, Math.Max(1, messages.Count / 100));
        _logger.LogInformation("Starting {ConsumerCount} consumer tasks for publishing", consumerCount);

        var consumerTasks = Enumerable.Range(0, consumerCount)
            .Select(index => ConsumeAndPublishMessagesAsync(channel.Reader, index))
            .ToArray();

        // Producer task to feed messages into the channel
        _logger.LogInformation("Starting to feed {Count} messages into the channel", messages.Count);
        var sw = Stopwatch.StartNew();

        foreach (var message in messages)
        {
            var beforeWrite = sw.ElapsedMilliseconds;
            await channel.Writer.WriteAsync(message);
            var afterWrite = sw.ElapsedMilliseconds;

            // If we waited more than 5ms to write, it indicates backpressure
            if (afterWrite - beforeWrite > 5)
            {
                channelBackpressureTime += (afterWrite - beforeWrite);
            }
        }

        // Signal completion
        channel.Writer.Complete();
        _logger.LogInformation("Finished feeding messages into the channel");

        // Wait for all consumers to complete
        await Task.WhenAll(consumerTasks);

        // Collect metrics from each consumer
        foreach (var task in consumerTasks)
        {
            var (success, failure) = ((int success, int failure))task.Result;
            successCount += success;
            failureCount += failure;
        }

        // Record final metrics
        TelemetryManager.RecordSnsPublishStats(successCount, failureCount);
        TelemetryManager.RecordChannelBackpressure(channelBackpressureTime);

        _logger.LogInformation(
            "Completed publishing: {SuccessCount} successful, {FailureCount} failed, {BackpressureMs}ms backpressure",
            successCount, failureCount, channelBackpressureTime);
    }

    private async Task<(int successCount, int failureCount)> ConsumeAndPublishMessagesAsync(
        ChannelReader<string> reader, int consumerId)
    {
        var successCount = 0;
        var failureCount = 0;

        _logger.LogDebug("Consumer {ConsumerId} started", consumerId);

        await foreach (var message in reader.ReadAllAsync())
        {
            try
            {
                var result = await _snsPublisher.PublishMessageAsync(message);

                if (result.IsSuccess)
                {
                    successCount++;

                    if (successCount % 100 == 0)
                    {
                        _logger.LogDebug("Consumer {ConsumerId} has published {SuccessCount} messages successfully",
                            consumerId, successCount);
                    }
                }
                else
                {
                    failureCount++;
                    _logger.LogWarning(
                        "Consumer {ConsumerId} failed to publish message: {ErrorMessage}",
                        consumerId, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(ex,
                    "Consumer {ConsumerId} encountered exception while publishing message",
                    consumerId);
            }
        }

        _logger.LogInformation(
            "Consumer {ConsumerId} completed: {SuccessCount} successful, {FailureCount} failed",
            consumerId, successCount, failureCount);

        return (successCount, failureCount);
    }
}
