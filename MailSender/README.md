# MailSender

MailSender is an ASP.NET Core .NET 10 API for sending individual and bulk email messages through multiple SMTP accounts. Each configured account has its own daily sending limit. When an account reaches its limit, MailSender uses the next account with available capacity. Daily counters reset automatically based on UTC date.

## Requirements

- .NET 10 SDK
- SMTP accounts that support authenticated SMTP
- App passwords where required by the email provider

## Configuration

Edit `MailSender/mailconfigs.json` before running the application. Do not commit real passwords or app passwords to source control.

```json
{
  "defaultDailyLimit": 100,
  "accounts": [
	{
	  "name": "Primary Gmail",
	  "host": "smtp.gmail.com",
	  "port": 587,
	  "enableSsl": true,
	  "username": "sender@example.com",
	  "password": "smtp-app-password",
	  "fromAddress": "sender@example.com",
	  "dailyLimit": 100
	}
  ]
}
```

For Gmail and other providers using STARTTLS, use port `587` with `enableSsl: true`. Port `465` uses implicit TLS and is not supported by `System.Net.Mail.SmtpClient` in the same way; prefer port 587 when the provider supports it.

## Run locally

From the solution directory:

```powershell
dotnet restore
dotnet run --project MailSender/MailSender.csproj
```

The API URL is shown in the console when the application starts. The configuration file is copied to the build output automatically.

## API endpoints

### Send one email

`POST /sendsingle`

```json
{
  "to": "recipient@example.com",
  "subject": "Welcome",
  "body": "<h1>Hello</h1><p>This message contains <strong>HTML</strong>.</p>",
  "isBodyHtml": true
}
```

`to` may also be an array of email addresses. The request supports `from`, `cc`, `bcc`, and file-path-based `attachments` in addition to `subject`, `body`, and `isBodyHtml`.

### Send multiple emails

`POST /sendbulk`

```json
[
  {
	"to": "first@example.com",
	"subject": "First message",
	"body": "<p>Hello first recipient.</p>",
	"isBodyHtml": true
  },
  {
	"to": "second@example.com",
	"subject": "Second message",
	"body": "Plain text message",
	"isBodyHtml": false
  }
]
```

The bulk response reports the number of successfully sent and failed messages. Sending is performed sequentially so the daily quota is tracked for every message.

## Daily quota and account rotation

- Accounts are checked in the order listed in `mailconfigs.json`.
- A successful send increments only the account used for that message.
- Once an account reaches `dailyLimit`, the next available account is selected.
- Counters reset automatically when the UTC date changes.
- Usage is held in memory and is reset if the application restarts.

## Windows Service deployment

The application is configured to run as a Windows Service named `MailSender`.

Publish the application:

```powershell
dotnet publish MailSender/MailSender.csproj -c Release -o .\publish
```

Copy `mailconfigs.json` into the published directory if it is not already present, then create the service from an elevated PowerShell prompt:

```powershell
sc.exe create MailSender binPath= "C:\Path\To\publish\MailSender.exe" start= auto
sc.exe start MailSender
```

Check the Windows Event Viewer and the service executable logs if the service stops during startup. The published directory must contain `mailconfigs.json`, and the service account must have permission to read it.

Remove the service with:

```powershell
sc.exe stop MailSender
sc.exe delete MailSender
```

## Security notes

- Treat SMTP app passwords as secrets.
- Do not expose `mailconfigs.json` publicly.
- Use environment variables or a secret store for production credentials where possible.
- Replace the sample credentials in the configuration before sending mail.
