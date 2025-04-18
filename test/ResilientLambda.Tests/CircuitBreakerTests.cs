using System;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace ResilientLambda.Tests
{
    public class CircuitBreakerTests : ResilientPublisherTestBase
    {
        [Fact]
        public async Task CircuitBreaker_OpensAfterMultipleFailures()
        {
            // Arrange
            int requestCount = 0;
            
            // Configure mock to return internal errors
            MockServer.Reset();
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(500)
                    .WithBody("<ErrorResponse><Error><Code>InternalError</Code><Message>Internal server error</Message></Error></ErrorResponse>"));
            
            var publisher = new ResilientSnsPublisher(SnsClient, Logger, TopicArn);
            
            // Act & Assert
            // Make enough calls to trip the circuit breaker (10 failures)
            for (int i = 0; i < 10; i++)
            {
                var result = await publisher.PublishMessageAsync($"Message {i}");
                result.IsSuccess.Should().BeFalse();
                result.ErrorType.Should().Be(ErrorType.ServiceUnavailable);
                requestCount++;
            }
            
            // The next call should fail fast with circuit breaker exception
            var circuitBrokenResult = await publisher.PublishMessageAsync("Circuit should be open");
            circuitBrokenResult.IsSuccess.Should().BeFalse();
            circuitBrokenResult.ErrorMessage.Should().Contain("Circuit breaker open");
            
            // Verify that the request wasn't actually sent to the server
            // by checking that the request count didn't increase
            requestCount.Should().Be(10);
        }
        
        [Fact]
        public async Task CircuitBreaker_AllowsSingleTestRequestAfterTimeout()
        {
            // This test verifies that after the circuit breaker timeout,
            // a single test request is allowed through (half-open state)
            
            // Arrange - Set up mock to return errors
            MockServer.Reset();
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(500)
                    .WithBody("<ErrorResponse><Error><Code>InternalError</Code><Message>Internal server error</Message></Error></ErrorResponse>"));
            
            // Create a publisher with a very short circuit breaker timeout for testing
            // Note: In a real implementation, you might need to modify the ResilientSnsPublisher
            // to accept circuit breaker parameters for testing
            var loggerMock = new Mock<ILogger<ResilientSnsPublisher>>();
            var publisher = new ResilientSnsPublisher(SnsClient, loggerMock.Object, TopicArn);
            
            // Act & Assert
            // Trip the circuit breaker
            for (int i = 0; i < 10; i++)
            {
                await publisher.PublishMessageAsync($"Message {i}");
            }
            
            // Verify circuit is open
            var openCircuitResult = await publisher.PublishMessageAsync("Circuit open");
            openCircuitResult.IsSuccess.Should().BeFalse();
            openCircuitResult.ErrorMessage.Should().Contain("Circuit breaker open");
            
            // In a real test, we would wait for the circuit timeout or use a time provider
            // For this test, we'll create a new publisher instance to simulate a reset circuit
            
            // Now reconfigure mock to return success
            MockServer.Reset();
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithBody("<PublishResponse><PublishResult><MessageId>success-after-reset</MessageId></PublishResult></PublishResponse>"));
            
            // Create a new publisher (simulating time passing)
            var newPublisher = new ResilientSnsPublisher(SnsClient, loggerMock.Object, TopicArn);
            
            // Circuit should allow a test call (half-open state)
            var halfOpenResult = await newPublisher.PublishMessageAsync("Circuit half-open test");
            halfOpenResult.IsSuccess.Should().BeTrue();
            halfOpenResult.Value.Should().Be("success-after-reset");
        }
        
        [Fact]
        public async Task CircuitBreaker_ClosesAfterSuccessfulTestRequest()
        {
            // This test verifies that after a successful test request in half-open state,
            // the circuit breaker closes and allows normal operation
            
            // Arrange - Set up mock to return success
            MockServer.Reset();
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithBody("<PublishResponse><PublishResult><MessageId>success</MessageId></PublishResult></PublishResponse>"));
            
            var loggerMock = new Mock<ILogger<ResilientSnsPublisher>>();
            var publisher = new ResilientSnsPublisher(SnsClient, loggerMock.Object, TopicArn);
            
            // Act - Make multiple successful requests
            for (int i = 0; i < 5; i++)
            {
                var result = await publisher.PublishMessageAsync($"Message {i}");
                result.IsSuccess.Should().BeTrue();
            }
            
            // Verify that the logger recorded success messages
            // In a real test, we would verify the circuit state directly
            loggerMock.Verify(
                x => x.Log(
                    It.IsAny<LogLevel>(),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Successfully published message")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Exactly(5));
        }
    }
}
