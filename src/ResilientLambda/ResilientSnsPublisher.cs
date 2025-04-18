

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Polly.Wrap;
using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace ResilientLambda;

public interface IResilientSnsPublisher
{
    Task<Result<string>> PublishMessageAsync(string message);
    Task<bool> IsHealthyAsync();
}

public class ResilientSnsPublisher : IResilientSnsPublisher
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly ILogger<ResilientSnsPublisher> _logger;
    private readonly string _topicArn;
    private readonly AsyncPolicyWrap<PublishResponse> _resiliencePolicy;

    public ResilientSnsPublisher(
        IAmazonSimpleNotificationService snsClient,
        ILogger<ResilientSnsPublisher> logger,
        string topicArn)
    {
        _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(topicArn))
            throw new ArgumentException("Topic ARN cannot be null or empty", nameof(topicArn));

        _topicArn = topicArn;
        _resiliencePolicy = CreateResiliencePolicy();
    }

    private AsyncPolicyWrap<PublishResponse> CreateResiliencePolicy()
    {
        // Handle transient errors with retries
        var retryPolicy = Policy<PublishResponse>
            .Handle<ThrottledException>()
            .Or<KMSThrottlingException>()
            .Or<InternalErrorException>()
            .Or<AmazonSimpleNotificationServiceException>(ex =>
                ex.StatusCode == HttpStatusCode.InternalServerError ||
                ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromMilliseconds(Math.Pow(2, retryAttempt) * 100),
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    _logger.LogWarning(
                        "Attempt {RetryAttempt} to publish to SNS failed. Waiting {TimespanMs}ms before retry. Error: {ErrorMessage}",
                        retryAttempt,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? "Unknown error");

                    // Record retry metric
                    TelemetryManager.RecordSnsRetry(
                        outcome.Exception?.GetType().Name ?? "Unknown",
                        retryAttempt);
                });

        // Circuit breaker to prevent overwhelming SNS when it's having issues
        var circuitBreakerPolicy = Policy
            .Handle<ThrottledException>()
            .Or<InternalErrorException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 10,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, breakDelay) =>
                {
                    _logger.LogError(
                        "Circuit broken due to SNS failures. Breaking for {BreakDelaySeconds} seconds. Last error: {ErrorMessage}",
                        breakDelay.TotalSeconds,
                        ex.Message);

                    // Record circuit break metric
                    TelemetryManager.RecordCircuitBreakerStateChange("open");
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit reset. SNS publishing resumed.");
                    TelemetryManager.RecordCircuitBreakerStateChange("closed");
                },
                onHalfOpen: () =>
                {
                    _logger.LogInformation("Circuit half-open. Testing SNS availability.");
                    TelemetryManager.RecordCircuitBreakerStateChange("half-open");
                })
            .AsAsyncPolicy<PublishResponse>();

        // Timeout policy to ensure we don't wait too long for AWS
        var timeoutPolicy = Policy.TimeoutAsync<PublishResponse>(
            timeout: TimeSpan.FromSeconds(5),
            onTimeoutAsync: async (context, timespan, task) =>
            {
                _logger.LogWarning("Timeout after {TimeoutSeconds} seconds when publishing to SNS",
                    timespan.TotalSeconds);

                // Record timeout metric
                TelemetryManager.RecordSnsTimeout();

                await Task.CompletedTask;
            });

        // Combine policies
        return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy, timeoutPolicy);
    }

    public async Task<Result<string>> PublishMessageAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.LogError("Cannot publish empty message");
            return Result<string>.Failure("Message cannot be null or empty", ErrorType.InvalidInput);
        }

        // Start activity for tracing
        using var activity = TelemetryManager.StartSnsPublishActivity(_topicArn);
        activity?.SetTag("sns.message_size", message.Length);

        // Start metrics collection
        TelemetryManager.RecordSnsPublishAttempt();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var request = new PublishRequest
            {
                TopicArn = _topicArn,
                Message = message
            };

            // Execute the request through the resilience pipeline
            var response = await _resiliencePolicy.ExecuteAsync(async () =>
                await _snsClient.PublishAsync(request));

            // Stop timing
            stopwatch.Stop();

            // Record success metrics
            TelemetryManager.RecordSnsPublishSuccess();
            TelemetryManager.RecordSnsPublishDuration(stopwatch.ElapsedMilliseconds);

            // Add result details to the activity
            activity?.SetTag("sns.message_id", response.MessageId);
            activity?.SetTag("otel.status_code", "OK");

            _logger.LogInformation("Successfully published message to SNS. MessageId: {MessageId}",
                response.MessageId);

            return Result<string>.Success(response.MessageId);
        }
        catch (Exception ex)
        {
            // Stop timing for error case
            stopwatch.Stop();

            // Record duration even for failures
            TelemetryManager.RecordSnsPublishDuration(stopwatch.ElapsedMilliseconds);

            // Categorize and record the error
            var (errorMessage, errorType) = CategorizeException(ex);

            // Record failure metrics
            TelemetryManager.RecordSnsPublishFailure(errorType.ToString());

            // Add error details to the activity
            activity?.SetTag("otel.status_code", "ERROR");
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);

            _logger.LogError(ex, "Failed to publish message to SNS: {ErrorMessage}", errorMessage);
            return Result<string>.Failure(errorMessage, ex, errorType);
        }
    }

    private (string ErrorMessage, ErrorType ErrorType) CategorizeException(Exception ex)
    {
        return ex switch
        {
            InvalidParameterException or InvalidParameterValueException =>
                ($"Invalid message format or attributes: {ex.Message}", ErrorType.InvalidInput),

            AuthorizationErrorException =>
                ($"Authorization failure: {ex.Message}", ErrorType.AuthorizationFailure),

            NotFoundException =>
                ($"Resource not found: {ex.Message}", ErrorType.ResourceNotFound),

            TimeoutRejectedException =>
                ($"Request timed out: {ex.Message}", ErrorType.ServiceUnavailable),

            BrokenCircuitException =>
                ($"Circuit breaker open - SNS service is currently unavailable", ErrorType.ServiceUnavailable),

            ThrottledException or KMSThrottlingException =>
                ($"Request throttled: {ex.Message}", ErrorType.ThrottlingError),

            InternalErrorException =>
                ($"AWS internal error: {ex.Message}", ErrorType.ServiceUnavailable),

            KMSDisabledException or KMSNotFoundException or KMSOptInRequiredException =>
               ($"KMS configuration error: {ex.Message}", ErrorType.ServiceUnavailable),

            _ => ($"Unexpected error: {ex.Message}", ErrorType.Unknown)
        };
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await _snsClient.GetTopicAttributesAsync(_topicArn);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SNS health check failed: {ErrorMessage}", ex.Message);
            return false;
        }
    }
}
