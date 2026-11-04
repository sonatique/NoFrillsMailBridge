# MailBridge

Read-only IMAP access to one or several mail accounts, exposed as an [MCP](https://modelcontextprotocol.io) server over stdio.

Each account gets a nickname and its own settings. Tools take one nickname, a comma separated list, or `all`, so a single search can span several mailboxes.

## Tools

| Tool | Parameters |
| --- | --- |
| `list_accounts` | none |
| `list_folders` | `account` |
| `search_messages` | `account`, `folder`, `from`, `subject`, `text`, `since`, `before`, `maxResults` |
| `read_message` | `account`, `folder`, `uid`, `maxChars` |
| `save_attachments` | `account`, `folder`, `uid` |

- `account` is a nickname, a comma separated list of nicknames, or `all` (default for the listing and search tools).
- `read_message` and `save_attachments` need exactly one account, because uids are only meaningful per account and folder.
- `search_messages` filters are optional and combined with AND. Dates use `yyyy-MM-dd`, and any other format is an error; `since` is inclusive, `before` is exclusive. Results are the most recent matches first.
- Attachments are the parts marked as attachment plus any other part with a file name, so inline PDFs and inline images are listed too.
- `save_attachments` writes to `<saveDir>/<nickname>/<yyyyMMdd>_uid<uid>/` and returns the file paths. A file that cannot be written is reported in the result and does not stop the others.

Folders are always opened read-only. Nothing is deleted, moved or sent. A failing account is reported under its own nickname without hiding the others.

## Build

Requires the .NET 10 SDK.

    dotnet build MailBridge.sln -c Release

Self-check of the parsing and file name rules:

    dotnet run --project tests/MailBridge.Tests -c Release

Single file executable for Windows:

    dotnet publish src/MailBridge/MailBridge.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out

The result is `out/MailBridge.exe`. It needs the .NET 10 runtime on the target machine.

## Configuration

Accounts are read from `%APPDATA%\MailBridge\accounts.json`, or from the path in the `MAILBRIDGE_CONFIG` environment variable. Set `MAILBRIDGE_CONFIG` explicitly on platforms other than Windows. See [`accounts.example.json`](accounts.example.json).

Top level:

| Key | Description |
| --- | --- |
| `saveDir` | Target directory of `save_attachments`. |
| `accounts` | List of accounts. |

Per account:

| Key | Description |
| --- | --- |
| `name` | Nickname used in tool calls. Required. |
| `host` | IMAP server. Required. |
| `user` | Login. Required. |
| `password` | Password in clear text. |
| `passwordEnv` | Name of an environment variable holding the password. |
| `port` | Default `993`. |
| `security` | `ssl` (default), `starttls` or `none`. |

One of `password` or `passwordEnv` is required. Prefer `passwordEnv`, and use an application specific password when your provider offers one.

The configuration is read on first use. A missing or invalid file is reported with its path in the tool error, and fixing the file is enough: no restart is needed.

## MCP client configuration

Declare the executable as a stdio server in your MCP client:

    {
      "mcpServers": {
        "mailbridge": {
          "command": "C:\\Tools\\MailBridge\\MailBridge.exe"
        }
      }
    }

Restart the client after changing its configuration. Logs are written to stderr, because stdout carries the protocol.

## Security notes

- Message content is returned to the MCP client as is. Treat it as untrusted input.
- Passwords stored in `accounts.json` are in clear text. Restrict the file permissions, or use `passwordEnv`.
- `accounts.json` is listed in `.gitignore`. Do not commit it.
- Attachments are saved with sanitized file names and are never executed.

## License

[MIT](LICENSE)
