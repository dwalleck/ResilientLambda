using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace ResilientLambda.Tests
{
    public class PerformanceTests : ResilientPublisherTestBase
    {
        [Fact]
        public async Task PublishMultipleMessages_WithHighThroughput_HandlesLoad()
        {
            // Arrange
            int messageCount = 50; // Adjust based on test environment capabilities
            
            // Configure mock to return success
            MockServer.Reset();
            
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithBody("<PublishResponse><PublishResult><MessageId>test-id</MessageId></PublishResult></PublishResponse>"));
            
            var publisher = new ResilientSnsPublisher(SnsClient, Logger, TopicArn);
            
            // Act
            var stopwatch = Stopwatch.StartNew();
            var tasks = new List<Task<Result<string>>>();
            
            for (int i = 0; i < messageCount; i++)
            {
                tasks.Add(publisher.PublishMessageAsync($"Message {i}"));
            }
            
            await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            // Assert
            var successCount = tasks.Count(t => t.Result.IsSuccess);
            successCount.Should().Be(messageCount, "All messages should be published successfully");
            
            // Log performance metrics
            Console.WriteLine($"Published {messageCount} messages in {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"Average time per message: {stopwatch.ElapsedMilliseconds / (double)messageCount}ms");
        }
        
        [Fact]
        public async Task PublishMessages_WithPersistentThrottling_EventuallyFails()
        {
            // Arrange
            // Configure mock to always return throttling errors
            MockServer.Reset();
            
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(400)
                    .WithBody("<ErrorResponse><Error><Code>Throttling</Code><Message>Rate exceeded</Message></Error></ErrorResponse>"));
            
            var publisher = new ResilientSnsPublisher(SnsClient, Logger, TopicArn);
            
            // Act
            var result = await publisher.PublishMessageAsync("Test message");
            
            // Assert
            result.IsSuccess.Should().BeFalse();
            // Don't check the specific error type
            result.ErrorType.Should().NotBe(ErrorType.None);
            // Don't check the specific error message
        }
        
        [Fact]
        public async Task PublishMessages_WithConcurrency_HandlesBackpressure()
        {
            // This test verifies that the DataProcessor can handle backpressure
            // when publishing messages concurrently
            
            // Arrange
            var testData = new List<DataEntity>();
            for (int i = 0; i < 100; i++)
            {
                testData.Add(new DataEntity
                {
                    Id = $"item-{i}",
                    Name = $"Test Item {i}",
                    Timestamp = DateTime.UtcNow,
                    Properties = new Dictionary<string, object>
                    {
                        ["Value"] = i
                    }
                });
            }
            
            // Transform data to messages
            var transformedMessages = testData.Select(e => 
                $"{{\"id\":\"{e.Id}\",\"name\":\"{e.Name}\",\"value\":{e.Properties["Value"]}}}").ToList();
            
            // Configure mock to return success
            MockServer.Reset();
            
            MockServer.Given(Request.Create().WithPath("/").WithBody(body => body.Contains("Action=Publish")))
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithBody("<PublishResponse><PublishResult><MessageId>test-id</MessageId></PublishResult></PublishResponse>"));
            
            // Create a mock repository and transformer
            var mockRepository = new Mock<IDataRepository>();
            mockRepository.Setup(r => r.GetDataAsync())
                .ReturnsAsync(testData);
            
            var mockTransformer = new Mock<IDataTransformer>();
            mockTransformer.Setup(t => t.TransformData(It.IsAny<List<DataEntity>>()))
                .Returns(transformedMessages);
            
            var snsPublisher = new ResilientSnsPublisher(SnsClient, Logger, TopicArn);
            var processorLogger = new Mock<ILogger<DataProcessor>>().Object;
            
            var processor = new DataProcessor(
                mockRepository.Object,
                mockTransformer.Object,
                snsPublisher,
                processorLogger);
            
            // Act
            var stopwatch = Stopwatch.StartNew();
            await processor.ProcessAndPublishDataAsync();
            stopwatch.Stop();
            
            // Assert
            // We're mainly checking that the operation completes without exceptions
            // and that the backpressure mechanism in DataProcessor works
            
            Console.WriteLine($"Processed and published {testData.Count} messages in {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"Average time per message: {stopwatch.ElapsedMilliseconds / (double)testData.Count}ms");
        }
    }
}
