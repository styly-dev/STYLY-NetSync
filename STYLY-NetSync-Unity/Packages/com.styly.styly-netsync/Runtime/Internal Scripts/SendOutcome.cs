// SendOutcome.cs - Represents the result of a send operation
// Distinguishes between "fatal" errors (requiring reconnection) and "backpressure" (temporary, will recover)

namespace Styly.NetSync
{
    /// <summary>
    /// Status of a send operation.
    /// </summary>
    internal enum SendStatus
    {
        /// <summary>Message was sent successfully.</summary>
        Sent,
        /// <summary>Message could not be sent due to backpressure (HWM reached, temporary). Not a disconnect.</summary>
        Backpressure,
        /// <summary>Fatal error occurred (exception, socket null). May indicate a disconnect.</summary>
        Fatal
    }

    /// <summary>
    /// Result of a send operation with status and optional error details.
    /// </summary>
    internal readonly struct SendOutcome
    {
        /// <summary>Status of the send operation.</summary>
        public readonly SendStatus Status;

        /// <summary>Error message when Status is Fatal, null otherwise.</summary>
        public readonly string Error;

        /// <summary>Returns true if the message was sent successfully.</summary>
        public bool IsSent => Status == SendStatus.Sent;

        /// <summary>Returns true if send failed due to backpressure (temporary, not a disconnect).</summary>
        public bool IsBackpressure => Status == SendStatus.Backpressure;

        /// <summary>Returns true if a fatal error occurred (may require reconnection).</summary>
        public bool IsFatal => Status == SendStatus.Fatal;

        private SendOutcome(SendStatus status, string error)
        {
            Status = status;
            Error = error;
        }

        /// <summary>Create a successful send outcome.</summary>
        public static SendOutcome Sent() => new SendOutcome(SendStatus.Sent, null);

        /// <summary>Create a backpressure outcome (temporary send failure, not a disconnect).</summary>
        public static SendOutcome Backpressure() => new SendOutcome(SendStatus.Backpressure, null);

        /// <summary>Create a fatal error outcome (may require reconnection).</summary>
        public static SendOutcome Fatal(string error) => new SendOutcome(SendStatus.Fatal, error ?? "unknown");
    }
}
