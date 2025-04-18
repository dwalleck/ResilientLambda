using System;

namespace ResilientLambda;

/// <summary>
/// Represents the result of an operation, encapsulating success or failure information
/// </summary>
public class Result<T>
{
    /// <summary>
    /// Indicates whether the operation was successful
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The value returned by the operation (if successful)
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// Error message (if operation failed)
    /// </summary>
    public string ErrorMessage { get; }

    /// <summary>
    /// The exception that caused the failure (if any)
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// The type of error that occurred
    /// </summary>
    public ErrorType ErrorType { get; }

    private Result(bool isSuccess, T value, string errorMessage, Exception exception, ErrorType errorType)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorMessage = errorMessage;
        Exception = exception;
        ErrorType = errorType;
    }

    /// <summary>
    /// Creates a successful result containing the specified value
    /// </summary>
    public static Result<T> Success(T value) =>
        new Result<T>(true, value, null, null, ErrorType.None);

    /// <summary>
    /// Creates a failure result with the specified error message and type
    /// </summary>
    public static Result<T> Failure(string errorMessage, ErrorType errorType) =>
        new Result<T>(false, default, errorMessage, null, errorType);

    /// <summary>
    /// Creates a failure result with the specified error message, exception, and type
    /// </summary>
    public static Result<T> Failure(string errorMessage, Exception exception, ErrorType errorType) =>
        new Result<T>(false, default, errorMessage, exception, errorType);

    /// <summary>
    /// Maps a successful result to a new Result containing a different value type
    /// </summary>
    public Result<TNew> Map<TNew>(Func<T, TNew> mapper)
    {
        if (IsSuccess)
        {
            return Result<TNew>.Success(mapper(Value));
        }

        return Result<TNew>.Failure(ErrorMessage, Exception, ErrorType);
    }

    /// <summary>
    /// Returns a string representation of the result
    /// </summary>
    public override string ToString()
    {
        if (IsSuccess)
        {
            return $"Success: {Value}";
        }

        return $"Failure ({ErrorType}): {ErrorMessage}";
    }
}

/// <summary>
/// Categorized error types for more meaningful error handling
/// </summary>
public enum ErrorType
{
    /// <summary>No error (success)</summary>
    None,

    /// <summary>Invalid input data</summary>
    InvalidInput,

    /// <summary>Authentication or authorization failure</summary>
    AuthorizationFailure,

    /// <summary>Resource not found</summary>
    ResourceNotFound,

    /// <summary>Service is currently unavailable</summary>
    ServiceUnavailable,

    /// <summary>Rate limiting or throttling</summary>
    ThrottlingError,

    /// <summary>Other unspecified error</summary>
    Unknown
}
