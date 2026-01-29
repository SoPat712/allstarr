namespace allstarr.Services.Common;

/// <summary>
/// Represents a typed error with code, message, and metadata
/// </summary>
public class Error
{
    /// <summary>
    /// Unique error code identifier
    /// </summary>
    public string Code { get; }
    
    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string Message { get; }
    
    /// <summary>
    /// Error type/category
    /// </summary>
    public ErrorType Type { get; }
    
    /// <summary>
    /// Additional metadata about the error
    /// </summary>
    public Dictionary<string, object>? Metadata { get; }
    
    private Error(string code, string message, ErrorType type, Dictionary<string, object>? metadata = null)
    {
        Code = code;
        Message = message;
        Type = type;
        Metadata = metadata;
    }
    
    /// <summary>
    /// Creates a Not Found error (404)
    /// </summary>
    public static Error NotFound(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "NOT_FOUND", message, ErrorType.NotFound, metadata);
    }
    
    /// <summary>
    /// Creates a Validation error (400)
    /// </summary>
    public static Error Validation(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "VALIDATION_ERROR", message, ErrorType.Validation, metadata);
    }
    
    /// <summary>
    /// Creates an Unauthorized error (401)
    /// </summary>
    public static Error Unauthorized(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "UNAUTHORIZED", message, ErrorType.Unauthorized, metadata);
    }
    
    /// <summary>
    /// Creates a Forbidden error (403)
    /// </summary>
    public static Error Forbidden(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "FORBIDDEN", message, ErrorType.Forbidden, metadata);
    }
    
    /// <summary>
    /// Creates a Conflict error (409)
    /// </summary>
    public static Error Conflict(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "CONFLICT", message, ErrorType.Conflict, metadata);
    }
    
    /// <summary>
    /// Creates an Internal Server Error (500)
    /// </summary>
    public static Error Internal(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "INTERNAL_ERROR", message, ErrorType.Internal, metadata);
    }
    
    /// <summary>
    /// Creates an External Service Error (502/503)
    /// </summary>
    public static Error ExternalService(string message, string? code = null, Dictionary<string, object>? metadata = null)
    {
        return new Error(code ?? "EXTERNAL_SERVICE_ERROR", message, ErrorType.ExternalService, metadata);
    }
    
    /// <summary>
    /// Creates a custom error with specified type
    /// </summary>
    public static Error Custom(string code, string message, ErrorType type, Dictionary<string, object>? metadata = null)
    {
        return new Error(code, message, type, metadata);
    }
}

/// <summary>
/// Categorizes error types for appropriate HTTP status code mapping
/// </summary>
public enum ErrorType
{
    /// <summary>
    /// Validation error (400 Bad Request)
    /// </summary>
    Validation,
    
    /// <summary>
    /// Resource not found (404 Not Found)
    /// </summary>
    NotFound,
    
    /// <summary>
    /// Authentication required (401 Unauthorized)
    /// </summary>
    Unauthorized,
    
    /// <summary>
    /// Insufficient permissions (403 Forbidden)
    /// </summary>
    Forbidden,
    
    /// <summary>
    /// Resource conflict (409 Conflict)
    /// </summary>
    Conflict,
    
    /// <summary>
    /// Internal server error (500 Internal Server Error)
    /// </summary>
    Internal,
    
    /// <summary>
    /// External service error (502 Bad Gateway / 503 Service Unavailable)
    /// </summary>
    ExternalService
}
