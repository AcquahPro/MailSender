using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MailSender";
});
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new MailAddressJsonConverter());
    options.SerializerOptions.Converters.Add(new MailMessageJsonConverter());
});

builder.Services.AddSingleton(provider =>
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "mailconfigs.json");
    if (!File.Exists(configPath))
    {
        configPath = Path.Combine(Directory.GetCurrentDirectory(), "mailconfigs.json");
    }

    return MailConfigurationLoader.Load(configPath);
});

builder.Services.AddSingleton<MailDispatchService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapGet("/health", () => "healthy");
app.MapPost("/sendsingle", async (MailMessage message, MailDispatchService mailDispatchService, CancellationToken cancellationToken) =>
{
    var validationError = ValidateMessage(message);
    if (validationError is not null)
    {
        return Results.BadRequest(validationError);
    }

    var result = await mailDispatchService.SendAsync(message, cancellationToken);
    if (result.IsSuccessful)
    {
        return Results.Ok(new
        {
            success = true,
            account = result.AccountName,
            sentAtUtc = result.SentAtUtc
        });
    }

    return Results.Problem(
        detail: result.ErrorMessage,
        statusCode: StatusCodes.Status429TooManyRequests,
        title: "Mail quota exceeded or SMTP delivery failed");
});

app.MapPost("/sendbulk", async (List<MailMessage> messages, MailDispatchService mailDispatchService, CancellationToken cancellationToken) =>
{
    if (messages is null || messages.Count == 0)
    {
        return Results.BadRequest("At least one email message is required.");
    }

    var results = new List<object>();
    var successCount = 0;

    foreach (var message in messages)
    {
        var validationError = ValidateMessage(message);
        if (validationError is not null)
        {
            results.Add(new { success = false, error = validationError });
            continue;
        }

        var result = await mailDispatchService.SendAsync(message, cancellationToken);
        if (result.IsSuccessful)
        {
            successCount++;
            results.Add(new { success = true, account = result.AccountName, sentAtUtc = result.SentAtUtc });
            continue;
        }

        results.Add(new { success = false, error = result.ErrorMessage });
    }

    return Results.Ok(new
    {
        success = successCount == messages.Count,
        sentCount = successCount,
        failedCount = messages.Count - successCount,
        results
    });
});

app.Run();

static string? ValidateMessage(MailMessage? message)
{
    if (message is null)
    {
        return "Mail message payload is required.";
    }

    if (message.To.Count == 0)
    {
        return "At least one recipient is required.";
    }

    if (string.IsNullOrWhiteSpace(message.Subject) && string.IsNullOrWhiteSpace(message.Body))
    {
        return "Email subject or body is required.";
    }

    return null;
}

internal sealed class MailConfigurationLoader
{
    public static MailConfiguration Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("mailconfigs.json was not found. Create the file in the app directory or output directory.", path);
        }

        var json = File.ReadAllText(path);
        var configuration = JsonSerializer.Deserialize<MailConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        });

        if (configuration is null)
        {
            throw new InvalidOperationException("The mail configuration file is empty or invalid.");
        }

        if (configuration.Accounts.Count == 0)
        {
            throw new InvalidOperationException("At least one email account must be configured in mailconfigs.json.");
        }

        foreach (var account in configuration.Accounts)
        {
            account.Name ??= account.Username;
            account.FromAddress ??= account.Username;
            account.DailyLimit = account.DailyLimit <= 0 ? configuration.DefaultDailyLimit : account.DailyLimit;
        }

        return configuration;
    }
}

internal sealed class MailConfiguration
{
    public int DefaultDailyLimit { get; set; } = 100;
    public List<MailAccountConfig> Accounts { get; set; } = [];
}

internal sealed class MailAccountConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public int DailyLimit { get; set; } = 100;
}

internal sealed class MailDispatchService
{
    private readonly MailConfiguration _configuration;
    private readonly Dictionary<string, DailyUsage> _dailyUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public MailDispatchService(MailConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<MailDispatchResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        var account = GetAvailableAccount();
        if (account is null)
        {
            return MailDispatchResult.Failure("No SMTP account has remaining daily quota.");
        }

        if (message.From is null || string.IsNullOrWhiteSpace(message.From.Address))
        {
            message.From = new MailAddress(account.FromAddress);
        }

        try
        {
            using var smtpClient = new SmtpClient(account.Host, account.Port)
            {
                EnableSsl = account.EnableSsl,
                Credentials = new NetworkCredential(account.Username, account.Password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };

            await smtpClient.SendMailAsync(message, cancellationToken);
            RecordUsage(account.Name);

            return MailDispatchResult.Success(account.Name);
        }
        catch (Exception ex)
        {
            return MailDispatchResult.Failure($"SMTP delivery failed for {account.Name}: {ex.Message}");
        }
    }

    private MailAccountConfig? GetAvailableAccount()
    {
        lock (_sync)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            foreach (var account in _configuration.Accounts)
            {
                if (!_dailyUsage.TryGetValue(account.Name, out var usage) || usage.Date != today)
                {
                    _dailyUsage[account.Name] = new DailyUsage { Date = today, Count = 0 };
                    usage = _dailyUsage[account.Name];
                }

                if (usage.Count < account.DailyLimit)
                {
                    return account;
                }
            }

            return null;
        }
    }

    private void RecordUsage(string accountName)
    {
        lock (_sync)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!_dailyUsage.TryGetValue(accountName, out var usage) || usage.Date != today)
            {
                usage = new DailyUsage { Date = today, Count = 0 };
            }

            usage.Count++;
            _dailyUsage[accountName] = usage;
        }
    }
}

internal sealed class MailDispatchResult
{
    public bool IsSuccessful { get; private set; }
    public string? AccountName { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime SentAtUtc { get; private set; } = DateTime.UtcNow;

    public static MailDispatchResult Success(string accountName)
    {
        return new MailDispatchResult { IsSuccessful = true, AccountName = accountName, SentAtUtc = DateTime.UtcNow };
    }

    public static MailDispatchResult Failure(string errorMessage)
    {
        return new MailDispatchResult { IsSuccessful = false, ErrorMessage = errorMessage };
    }
}

internal sealed class DailyUsage
{
    public DateOnly Date { get; set; }
    public int Count { get; set; }
}

internal sealed class MailAddressJsonConverter : JsonConverter<MailAddress>
{
    public override MailAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new MailAddress(value);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.TryGetProperty("address", out var addressElement) && addressElement.ValueKind == JsonValueKind.String)
            {
                var address = addressElement.GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    var displayName = root.TryGetProperty("displayName", out var displayNameElement)
                        && displayNameElement.ValueKind == JsonValueKind.String
                        ? displayNameElement.GetString()
                        : null;

                    return new MailAddress(address, displayName);
                }
            }
        }

        throw new JsonException("A valid email address is required.");
    }

    public override void Write(Utf8JsonWriter writer, MailAddress value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Address);
    }
}

internal sealed class MailMessageJsonConverter : JsonConverter<MailMessage>
{
    public override MailMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var message = new MailMessage();

        if (root.TryGetProperty("from", out var fromElement))
        {
            var fromAddress = ReadAddress(fromElement);
            if (fromAddress is not null)
            {
                message.From = fromAddress;
            }
        }

        if (root.TryGetProperty("to", out var toElement))
        {
            foreach (var address in ReadAddresses(toElement))
            {
                message.To.Add(address);
            }
        }

        if (root.TryGetProperty("cc", out var ccElement))
        {
            foreach (var address in ReadAddresses(ccElement))
            {
                message.CC.Add(address);
            }
        }

        if (root.TryGetProperty("bcc", out var bccElement))
        {
            foreach (var address in ReadAddresses(bccElement))
            {
                message.Bcc.Add(address);
            }
        }

        if (root.TryGetProperty("subject", out var subjectElement) && subjectElement.ValueKind == JsonValueKind.String)
        {
            message.Subject = subjectElement.GetString() ?? string.Empty;
        }

        string? bodyText = null;
        if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String)
        {
            bodyText = bodyElement.GetString();
            message.Body = bodyText ?? string.Empty;
        }

        // 1. Respect explicit flag if provided
        if (root.TryGetProperty("isBodyHtml", out var isBodyHtmlElement))
        {
            message.IsBodyHtml = isBodyHtmlElement.ValueKind == JsonValueKind.True;
        }
        // 2. Auto-detect HTML if the flag was omitted but the body looks like HTML
        else if (!string.IsNullOrWhiteSpace(bodyText) && ContainsHtmlTags(bodyText))
        {
            message.IsBodyHtml = true;
        }

        if (root.TryGetProperty("attachments", out var attachmentsElement) && attachmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachmentsElement.EnumerateArray())
            {
                if (attachment.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(attachment.GetString()))
                {
                    message.Attachments.Add(new Attachment(attachment.GetString()!));
                }
            }
        }

        return message;
    }

    public override void Write(Utf8JsonWriter writer, MailMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("subject", value.Subject);
        writer.WriteString("body", value.Body);
        writer.WriteBoolean("isBodyHtml", value.IsBodyHtml); // This will now accurately reflect auto-detected state
        writer.WriteString("from", value.From?.Address);

        writer.WriteStartArray("to");
        foreach (var recipient in value.To)
        {
            writer.WriteStringValue(recipient.Address);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("cc");
        foreach (var recipient in value.CC)
        {
            writer.WriteStringValue(recipient.Address);
        }
        writer.WriteEndArray();

        writer.WriteStartArray("bcc");
        foreach (var recipient in value.Bcc)
        {
            writer.WriteStringValue(recipient.Address);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static bool ContainsHtmlTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Regex to check for common HTML tags (case-insensitive)
        // Matches <html, <body, <div, <p, <table, <tr, <td, <span, <a, <img, <h1, etc.
        return System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"<\s*(html|body|div|p|table|tr|td|th|span|a|img|h[1-6])\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static MailAddress? ReadAddress(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            return string.IsNullOrWhiteSpace(raw) ? null : new MailAddress(raw);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("address", out var addressElement) && addressElement.ValueKind == JsonValueKind.String)
            {
                var address = addressElement.GetString();
                if (!string.IsNullOrWhiteSpace(address))
                {
                    var displayName = element.TryGetProperty("displayName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;
                    return new MailAddress(address, displayName);
                }
            }
        }

        return null;
    }

    private static IEnumerable<MailAddress> ReadAddresses(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                yield return new MailAddress(raw);
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var address = ReadAddress(item);
                if (address is not null)
                {
                    yield return address;
                }
            }
        }
    }
}