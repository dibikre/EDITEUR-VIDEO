namespace VideoSplitter.Services;

public sealed class MediaProbeException : Exception
{
    public string UserMessage { get; }
    public string? TechnicalDetails { get; }

    public MediaProbeException(string userMessage, string? technicalDetails = null, Exception? innerException = null)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
        TechnicalDetails = technicalDetails;
    }
}
