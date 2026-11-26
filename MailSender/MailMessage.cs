namespace MailSender
{
    public record MailMessage(string To, string Subject, string Body)
    {
        public bool IsBodyHtml { get; init; } = true;
    }
}
