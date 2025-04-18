using Xunit;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Threading.Tasks;

namespace ResilientLambda.Tests;

public class FunctionTest
{
    [Fact]
    public async Task TestFunctionHandler()
    {
        // Arrange
        // Create a mock for IDataProcessor
        var mockDataProcessor = new Mock<IDataProcessor>();
        mockDataProcessor.Setup(p => p.ProcessAndPublishDataAsync())
            .Returns(Task.CompletedTask);

        // Create a service provider with the mock
        var services = new ServiceCollection();
        services.AddSingleton(mockDataProcessor.Object);
        services.AddLogging();

        // Create a function with the mock service provider
        var function = new Function(services.BuildServiceProvider());
        var context = new TestLambdaContext();

        // Act
        await function.FunctionHandler(new { }, context);

        // Assert
        // Verify that the data processor was called
        mockDataProcessor.Verify(p => p.ProcessAndPublishDataAsync(), Times.Once);
    }
}
