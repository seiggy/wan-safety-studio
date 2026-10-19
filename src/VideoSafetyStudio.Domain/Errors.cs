namespace VideoSafetyStudio.Domain;

/// <summary>Input failed a business rule. Surfaces as HTTP 400.</summary>
public class ValidationException(string message) : Exception(message);

/// <summary>A referenced item does not exist. Surfaces as HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>The request conflicts with current state. Surfaces as HTTP 409.</summary>
public class ConflictException(string message) : Exception(message);
