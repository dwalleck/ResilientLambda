using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ResilientLambda;

public static class TelemetryConstants
{
    public const string ServiceName = "DataProcessingService";
}

public static class TelemetryManager
{
    // Activity Source for distributed tracing
    private static readonly ActivitySource ActivitySource = new ActivitySource(TelemetryConstants.ServiceName);

    // Meters for metrics
    private static readonly Meter Meter = new Meter(TelemetryConstants.ServiceName);

    // SNS Metrics
    private static readonly Counter<long> SnsPublishAttempts = Meter.CreateCounter<long>(
        "sns_publish_attempts", "{count}", "Number of attempts to publish to SNS");

    private static readonly Counter<long> SnsPublishSuccesses = Meter.CreateCounter<long>(
        "sns_publish_successes", "{count}", "Number of successful SNS publish operations");

    private static readonly Counter<long> SnsPublishFailures = Meter.CreateCounter<long>(
        "sns_publish_failures", "{count}", "Number of failed SNS publish operations");

    private static readonly Histogram<double> SnsPublishDuration = Meter.CreateHistogram<double>(
        "sns_publish_duration", "ms", "Duration of SNS publish operations");

    private static readonly Counter<long> SnsRetries = Meter.CreateCounter<long>(
        "sns_retries", "{count}", "Number of SNS publish retries");

    private static readonly Counter<long> SnsTimeouts = Meter.CreateCounter<long>(
        "sns_timeouts", "{count}", "Number of SNS publish timeouts");

    private static readonly Counter<long> CircuitBreakerStateChanges = Meter.CreateCounter<long>(
        "circuit_breaker_state_changes", "{count}", "Number of circuit breaker state changes");

    // Data Processing Metrics
    private static readonly Counter<long> DataItemsRetrieved = Meter.CreateCounter<long>(
        "data_items_retrieved", "{count}", "Number of data items retrieved from the database");

    private static readonly Counter<long> DataItemsTransformed = Meter.CreateCounter<long>(
        "data_items_transformed", "{count}", "Number of data items transformed");

    private static readonly Histogram<double> TotalProcessingTime = Meter.CreateHistogram<double>(
        "total_processing_time", "ms", "Total time for processing and publishing all data");

    private static readonly Histogram<double> ChannelBackpressureTime = Meter.CreateHistogram<double>(
        "channel_backpressure_time", "ms", "Time spent waiting due to channel backpressure");

    // Activity methods
    public static Activity StartActivity(string name)
    {
        return ActivitySource.StartActivity(name, ActivityKind.Internal);
    }

    public static Activity StartSnsPublishActivity(string topicArn)
    {
        var activity = ActivitySource.StartActivity("SnsPublish", ActivityKind.Client);
        activity?.SetTag("messaging.system", "aws.sns");
        activity?.SetTag("messaging.destination", topicArn);
        activity?.SetTag("messaging.destination_kind", "topic");
        return activity;
    }

    // SNS metric recording methods
    public static void RecordSnsPublishAttempt()
    {
        SnsPublishAttempts.Add(1);
    }

    public static void RecordSnsPublishSuccess()
    {
        SnsPublishSuccesses.Add(1);
    }

    public static void RecordSnsPublishFailure(string errorType)
    {
        SnsPublishFailures.Add(1, new KeyValuePair<string, object>("error_type", errorType));
    }

    public static void RecordSnsPublishDuration(double milliseconds)
    {
        SnsPublishDuration.Record(milliseconds);
    }

    public static void RecordSnsRetry(string errorType, int attemptNumber)
    {
        SnsRetries.Add(1, new KeyValuePair<string, object>[]
        {
            new("error_type", errorType),
            new("attempt_number", attemptNumber)
        });
    }

    public static void RecordSnsTimeout()
    {
        SnsTimeouts.Add(1);
    }

    public static void RecordCircuitBreakerStateChange(string state)
    {
        CircuitBreakerStateChanges.Add(1, new KeyValuePair<string, object>("state", state));
    }

    // Data processing metric recording methods
    public static void RecordDataItemsRetrieved(int count)
    {
        DataItemsRetrieved.Add(count);
    }

    public static void RecordDataItemsTransformed(int count)
    {
        DataItemsTransformed.Add(count);
    }

    public static void RecordTotalProcessingTime(double milliseconds)
    {
        TotalProcessingTime.Record(milliseconds);
    }

    public static void RecordChannelBackpressure(double milliseconds)
    {
        ChannelBackpressureTime.Record(milliseconds);
    }

    public static void RecordSnsPublishStats(int successCount, int failureCount)
    {
        var totalAttempts = successCount + failureCount;
        if (totalAttempts > 0)
        {
            SnsPublishSuccesses.Add(successCount);
            SnsPublishFailures.Add(failureCount);
        }
    }
}
