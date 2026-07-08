using System.Runtime.Serialization;

namespace BoardSync.Api.Shared.Kernel.Exceptions;

/// <summary>Base class for all domain-specific exceptions.</summary>
[Serializable]
public class DomainException : Exception
{
    public DomainException() { }
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
    protected DomainException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>Thrown when a requested resource does not exist.</summary>
[Serializable]
public class NotFoundException : DomainException
{
    public NotFoundException() { }
    public NotFoundException(string message) : base(message) { }
    public NotFoundException(string message, Exception inner) : base(message, inner) { }
    protected NotFoundException(SerializationInfo info, StreamingContext context) : base(info, context) { }

    public NotFoundException(string resourceName, object id)
        : base($"{resourceName} with id '{id}' was not found.") { }
}

/// <summary>Thrown when an operation is not permitted for the current user.</summary>
[Serializable]
public class ForbiddenException : DomainException
{
    public ForbiddenException() : base("You do not have permission to perform this action.") { }
    public ForbiddenException(string message) : base(message) { }
    public ForbiddenException(string message, Exception inner) : base(message, inner) { }
    protected ForbiddenException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>Thrown when business rules are violated.</summary>
[Serializable]
public class BusinessRuleException : DomainException
{
    public BusinessRuleException() { }
    public BusinessRuleException(string message) : base(message) { }
    public BusinessRuleException(string message, Exception inner) : base(message, inner) { }
    protected BusinessRuleException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}

/// <summary>Thrown when a conflicting resource already exists.</summary>
[Serializable]
public class ConflictException : DomainException
{
    public ConflictException() { }
    public ConflictException(string message) : base(message) { }
    public ConflictException(string message, Exception inner) : base(message, inner) { }
    protected ConflictException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}
