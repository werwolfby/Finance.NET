using System;

namespace Finance.Net.Exceptions;

/// <summary>
/// Represents a request the provider rejected as invalid - a range outside the window it
/// keeps, an unsupported granularity, a malformed parameter.
/// </summary>
/// <remarks>
/// This is a permanent answer, not a transient transport failure: the retry policy does
/// not retry it. It derives from <see cref="FinanceNetException"/>, so existing handlers
/// that catch <see cref="FinanceNetException"/> keep working unchanged.
/// </remarks>
public class FinanceNetInvalidRequestException : FinanceNetException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FinanceNetInvalidRequestException"/> class.
    /// </summary>
    public FinanceNetInvalidRequestException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FinanceNetInvalidRequestException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public FinanceNetInvalidRequestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FinanceNetInvalidRequestException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception.</param>
    public FinanceNetInvalidRequestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
