namespace BoardSync.Api.Shared.Kernel.Exceptions;

/// <summary>Base class for all domain-specific exceptions.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a requested resource does not exist.</summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string resourceName, object id)
        : base($"{resourceName} with id '{id}' was not found.") { }

    public NotFoundException(string message) : base(message) { }
}

/// <summary>Thrown when an operation is not permitted for the current user.</summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : base(message) { }
}

/// <summary>Thrown when business rules are violated.</summary>
public class BusinessRuleException : DomainException
{
    public BusinessRuleException(string message) : base(message) { }
}

/// <summary>Thrown when a conflicting resource already exists.</summary>
public class ConflictException : DomainException
{
    public ConflictException(string message) : base(message) { }
}
