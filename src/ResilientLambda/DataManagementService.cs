using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace ResilientLambda;

// Data Models
public class DataEntity
{
    public string Id { get; set; }
    public string Name { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object> Properties { get; set; }
}

public class TransformedMessage
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string FormattedTimestamp { get; set; }
    public Dictionary<string, string> NormalizedProperties { get; set; }
    public string Source { get; set; } = "DataProcessingService";
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}

// Repository Interface
public interface IDataRepository
{
    Task<List<DataEntity>> GetDataAsync();
}

// Data Repository Implementation
public class DataRepository : IDataRepository
{
    private readonly ILogger<DataRepository> _logger;
    // Add database connection or ORM context here

    public DataRepository(ILogger<DataRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<DataEntity>> GetDataAsync()
    {
        using var activity = TelemetryManager.StartActivity("DatabaseQuery");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Querying database for data");

            // Implement your actual database query here
            // Example:
            // using var connection = new SqlConnection(_connectionString);
            // await connection.OpenAsync();
            // var command = new SqlCommand("SELECT * FROM YourTable WHERE LastProcessed < @cutoffDate", connection);
            // command.Parameters.AddWithValue("@cutoffDate", DateTime.UtcNow.AddDays(-1));
            // var reader = await command.ExecuteReaderAsync();

            // For demonstration, we'll return dummy data
            await Task.Delay(100); // Simulate database query time

            var result = new List<DataEntity>();
            for (int i = 0; i < 1000; i++)
            {
                result.Add(new DataEntity
                {
                    Id = $"item-{i}",
                    Name = $"Test Item {i}",
                    Timestamp = DateTime.UtcNow.AddHours(-i % 24),
                    Properties = new Dictionary<string, object>
                    {
                        ["Value"] = i * 1.5,
                        ["Category"] = i % 5,
                        ["IsActive"] = i % 3 == 0
                    }
                });
            }

            stopwatch.Stop();
            activity?.SetTag("db.result_count", result.Count);
            activity?.SetTag("db.execution_time_ms", stopwatch.ElapsedMilliseconds);

            _logger.LogInformation("Retrieved {Count} records from database in {ElapsedMs}ms",
                result.Count, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error querying database: {ErrorMessage}", ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

// Transformer Interface
public interface IDataTransformer
{
    List<string> TransformData(List<DataEntity> entities);
}

// Data Transformer Implementation
public class DataTransformer : IDataTransformer
{
    private readonly ILogger<DataTransformer> _logger;

    public DataTransformer(ILogger<DataTransformer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<string> TransformData(List<DataEntity> entities)
    {
        using var activity = TelemetryManager.StartActivity("TransformData");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Transforming {Count} data entities", entities.Count);

            var result = new List<string>();

            foreach (var entity in entities)
            {
                // Transform the entity into the target format
                var transformed = new TransformedMessage
                {
                    Id = entity.Id,
                    Name = entity.Name,
                    FormattedTimestamp = entity.Timestamp.ToString("o"),
                    NormalizedProperties = NormalizeProperties(entity.Properties)
                };

                // Serialize to JSON
                var json = JsonSerializer.Serialize(transformed, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                result.Add(json);
            }

            stopwatch.Stop();
            activity?.SetTag("transform.count", result.Count);
            activity?.SetTag("transform.execution_time_ms", stopwatch.ElapsedMilliseconds);

            _logger.LogInformation("Transformed {Count} entities into messages in {ElapsedMs}ms",
                result.Count, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error transforming data: {ErrorMessage}", ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private Dictionary<string, string> NormalizeProperties(Dictionary<string, object> properties)
    {
        var result = new Dictionary<string, string>();

        foreach (var kvp in properties)
        {
            // Convert all values to strings for consistent handling
            result[kvp.Key] = kvp.Value?.ToString() ?? "null";
        }

        return result;
    }
}