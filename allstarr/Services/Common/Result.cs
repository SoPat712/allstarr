namespace allstarr.Services.Common;

/// <summary>
/// Represents the result of an operation that can either succeed with a value or fail with an error.
/// This pattern allows explicit error handling without using exceptions for control flow.
/// </summary>
/// <typeparam name="T">The type of the value returned on success</typeparam>
public class Result<T>
{
    /// <summary>
    /// Indicates whether the operation succeeded
    /// </summary>
    public bool IsSuccess { get; }
    
    /// <summary>
    /// Indicates whether the operation failed
    /// </summary>
    public bool IsFailure => !IsSuccess;
    
    /// <summary>
    /// The value returned on success (null if failed)
    /// </summary>
    public T? Value { get; }
    
    /// <summary>
    /// The error that occurred on failure (null if succeeded)
    /// </summary>
    public Error? Error { get; }
    
    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }
    
    /// <summary>
    /// Creates a successful result with a value
    /// </summary>
    public static Result<T> Success(T value)
    {
        return new Result<T>(true, value, null);
    }
    
    /// <summary>
    /// Creates a failed result with an error
    /// </summary>
    public static Result<T> Failure(Error error)
    {
        return new Result<T>(false, default, error);
    }
    
    /// <summary>
    /// Implicit conversion from T to Result&lt;T&gt; for convenience
    /// </summary>
    public static implicit operator Result<T>(T value)
    {
        return Success(value);
    }
    
    /// <summary>
    /// Implicit conversion from Error to Result&lt;T&gt; for convenience
    /// </summary>
    public static implicit operator Result<T>(Error error)
    {
        return Failure(error);
    }
}

/// <summary>
/// Non-generic Result for operations that don't return a value
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }
    
    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }
    
    public static Result Success()
    {
        return new Result(true, null);
    }
    
    public static Result Failure(Error error)
    {
        return new Result(false, error);
    }
    
    public static implicit operator Result(Error error)
    {
        return Failure(error);
    }
}
