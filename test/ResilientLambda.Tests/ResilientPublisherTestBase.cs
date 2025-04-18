using System;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Moq;
using WireMock.Server;
using Xunit;

namespace ResilientLambda.Tests
{
    public abstract class ResilientPublisherTestBase : IDisposable
    {
        protected readonly WireMockServer MockServer;
        protected readonly ILogger<ResilientSnsPublisher> Logger;
        protected readonly string TopicArn = "arn:aws:sns:us-east-1:123456789012:test-topic";
        protected IAmazonSimpleNotificationService SnsClient;
        
        protected ResilientPublisherTestBase()
        {
            // Setup WireMock server
            MockServer = WireMockServer.Start();
            
            // Configure AWS SNS client to use our mock server
            var config = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = MockServer.Urls[0],
                UseHttp = true
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(
                "FAKE_ACCESS_KEY", 
                "FAKE_SECRET_KEY", 
                config);
            
            // Setup logger
            var loggerMock = new Mock<ILogger<ResilientSnsPublisher>>();
            Logger = loggerMock.Object;
        }
        
        public void Dispose()
        {
            MockServer.Stop();
            SnsClient.Dispose();
        }
    }
}
