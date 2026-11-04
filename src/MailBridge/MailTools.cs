using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MailBridge;

[McpServerToolType]
public static class MailTools
{
    private const string AccountHelp = "Account name, a comma separated list of names, or 'all'.";
    private const int ConnectTimeoutMs = 30_000;

    // Stays under the 255 byte file name limit of Linux file systems, even with 3 byte characters.
    private const int MaxFileNameLength = 80;

    [McpServerTool(Name = "list_accounts"), Description("Lists the configured mail accounts.")]
    public static string ListAccounts() =>
        string.Join('\n', Config.Current.Accounts.Select(a => $"{a.Name} ({a.User} on {a.Host})"));

    [McpServerTool(Name = "list_folders"), Description("Lists all folders of the selected accounts.")]
    public static async Task<string> ListFolders(
        [Description(AccountHelp)] string account = "all",
        CancellationToken ct = default)
    {
        var parts = await Task.WhenAll(Config.Resolve(account).Select(a => PerAccount(a, async () =>
        {
            using var client = await ConnectAsync(a, ct);
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task Walk(IMailFolder folder)
            {
                if (!names.Add(folder.FullName))
                    return;
                foreach (var child in await folder.GetSubfoldersAsync(false, ct))
                    await Walk(child);
            }

            foreach (var top in await client.GetFoldersAsync(client.PersonalNamespaces[0], false, ct))
                await Walk(top);

            await client.DisconnectAsync(true, ct);
            return string.Join('\n', names);
        })));

        return string.Join("\n\n", parts);
    }

    [McpServerTool(Name = "search_messages"),
     Description("Searches a folder of the selected accounts and returns the most recent matches per account " +
                 "(uid, date, sender, subject, attachment names). All filters are optional and combined with AND. " +
                 "Dates use yyyy-MM-dd; 'since' is inclusive, 'before' is exclusive.")]
    public static async Task<string> SearchMessages(
        [Description(AccountHelp)] string account = "all",
        [Description("Folder full name, for example INBOX.")] string folder = "INBOX",
        [Description("Text contained in the sender address or name.")] string? from = null,
        [Description("Text contained in the subject.")] string? subject = null,
        [Description("Text contained in the subject or the body.")] string? text = null,
        [Description("Earliest delivery date, yyyy-MM-dd.")] string? since = null,
        [Description("Delivery date upper bound, yyyy-MM-dd.")] string? before = null,
        [Description("Maximum number of results per account.")] int maxResults = 30,
        CancellationToken ct = default)
    {
        var sinceDate = ParseDate(since, nameof(since));
        var beforeDate = ParseDate(before, nameof(before));

        var parts = await Task.WhenAll(Config.Resolve(account).Select(a => PerAccount(a, async () =>
        {
            using var client = await ConnectAsync(a, ct);
            var box = await client.GetFolderAsync(folder, ct);
            await box.OpenAsync(FolderAccess.ReadOnly, ct);

            var query = SearchQuery.All;
            if (!string.IsNullOrWhiteSpace(from))
                query = query.And(SearchQuery.FromContains(from));
            if (!string.IsNullOrWhiteSpace(subject))
                query = query.And(SearchQuery.SubjectContains(subject));
            if (!string.IsNullOrWhiteSpace(text))
                query = query.And(SearchQuery.SubjectContains(text).Or(SearchQuery.BodyContains(text)));
            if (sinceDate is { } s)
                query = query.And(SearchQuery.DeliveredAfter(s));
            if (beforeDate is { } b)
                query = query.And(SearchQuery.DeliveredBefore(b));

            var found = await box.SearchAsync(query, ct);
            var uids = found.Reverse().Take(Math.Clamp(maxResults, 1, 200)).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"{found.Count} match(es), showing {uids.Count} most recent.");

            if (uids.Count > 0)
            {
                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                            MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;

                foreach (var m in (await box.FetchAsync(uids, items, ct)).OrderByDescending(x => x.UniqueId.Id))
                {
                    var date = (m.Envelope?.Date ?? m.InternalDate)?.ToString("yyyy-MM-dd HH:mm");
                    var files = string.Join(", ", m.BodyParts
                        .Where(x => x.IsAttachment || x.FileName is not null)
                        .Select(x => string.IsNullOrWhiteSpace(x.FileName) ? "attachment" : x.FileName));
                    sb.AppendLine(OneLine($"uid={m.UniqueId.Id} | {date} | {m.Envelope?.From} | {m.Envelope?.Subject}" +
                                          (files.Length > 0 ? $" | attachments: {files}" : "")));
                }
            }

            await client.DisconnectAsync(true, ct);
            return sb.ToString().TrimEnd();
        })));

        return string.Join("\n\n", parts);
    }

    [McpServerTool(Name = "read_message"), Description("Reads one message: headers, attachment names and text body.")]
    public static async Task<string> ReadMessage(
        [Description("Account name.")] string account,
        [Description("Folder full name.")] string folder,
        [Description("Message uid as returned by search_messages.")] uint uid,
        [Description("Maximum number of body characters returned.")] int maxChars = 8000,
        CancellationToken ct = default)
    {
        var a = Config.ResolveOne(account);
        return await WithRealError(async () =>
        {
            using var client = await ConnectAsync(a, ct);
            var box = await client.GetFolderAsync(folder, ct);
            await box.OpenAsync(FolderAccess.ReadOnly, ct);
            var msg = await box.GetMessageAsync(new UniqueId(uid), ct);
            await client.DisconnectAsync(true, ct);

            var body = string.IsNullOrWhiteSpace(msg.TextBody) ? HtmlToText(msg.HtmlBody) ?? "" : msg.TextBody;
            if (body.Length > maxChars)
                body = body[..maxChars] + "\n[truncated]";

            var files = AttachmentParts(msg).Select(AttachmentName).ToList();

            var sb = new StringBuilder();
            void Header(string line) => sb.AppendLine(OneLine(line));
            Header($"Account: {a.Name}");
            Header($"From: {msg.From}");
            Header($"To: {msg.To}");
            if (msg.Cc.Count > 0)
                Header($"Cc: {msg.Cc}");
            Header($"Date: {msg.Date:yyyy-MM-dd HH:mm}");
            Header($"Subject: {msg.Subject}");
            Header($"Attachments: {(files.Count == 0 ? "none" : string.Join(", ", files))}");
            sb.AppendLine();
            sb.Append(body);
            return sb.ToString();
        });
    }

    [McpServerTool(Name = "save_attachments"),
     Description("Saves the attachments of one message under the configured save directory and returns the file paths.")]
    public static async Task<string> SaveAttachments(
        [Description("Account name.")] string account,
        [Description("Folder full name.")] string folder,
        [Description("Message uid as returned by search_messages.")] uint uid,
        CancellationToken ct = default)
    {
        var saveDir = Config.Current.SaveDir;
        if (string.IsNullOrWhiteSpace(saveDir))
            return "saveDir is not set in the configuration file.";

        var a = Config.ResolveOne(account);
        return await WithRealError(async () =>
        {
            using var client = await ConnectAsync(a, ct);
            var box = await client.GetFolderAsync(folder, ct);
            await box.OpenAsync(FolderAccess.ReadOnly, ct);
            var msg = await box.GetMessageAsync(new UniqueId(uid), ct);
            await client.DisconnectAsync(true, ct);

            var dir = Path.Combine(saveDir, a.Name, $"{msg.Date:yyyyMMdd}_uid{uid}");
            Directory.CreateDirectory(dir);

            var saved = new List<string>();
            foreach (var entity in AttachmentParts(msg))
            {
                var name = SafeName(AttachmentName(entity));
                try
                {
                    var path = UniquePath(dir, name);
                    // Closed before the path is listed: the final flush can still fail on a full disk.
                    await using (var stream = File.Create(path))
                    {
                        switch (entity)
                        {
                            case MessagePart mp:
                                if (mp.Message is not null)
                                    await mp.Message.WriteToAsync(stream, ct);
                                break;
                            case MimePart part:
                                if (part.Content is not null)
                                    await part.Content.DecodeToAsync(stream, ct);
                                break;
                            default:
                                continue;
                        }
                    }

                    saved.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One unwritable file must not lose the others or the paths already saved.
                    saved.Add($"Error saving '{name}': {ex.Message}");
                }
            }

            return saved.Count == 0 ? "No attachments." : string.Join('\n', saved);
        });
    }

    // Isolates failures so one unreachable account does not hide the others.
    private static async Task<string> PerAccount(AccountConfig account, Func<Task<string>> work)
    {
        try
        {
            return $"[{account.Name}]\n{await work()}";
        }
        catch (Exception ex)
        {
            return $"[{account.Name}]\nError: {ex.Message}";
        }
    }

    // The MCP library replaces the text of any exception but McpException, which would hide the cause from the client.
    private static async Task<string> WithRealError(Func<Task<string>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException))
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static async Task<ImapClient> ConnectAsync(AccountConfig account, CancellationToken ct)
    {
        var password = account.ResolvePassword();
        var client = new ImapClient();
        var commandTimeout = client.Timeout;
        client.Timeout = ConnectTimeoutMs;
        try
        {
            await client.ConnectAsync(account.Host, account.Port, account.SocketOptions, ct);
            await client.AuthenticateAsync(account.User, password, ct);
            // A full text search can rightly stay silent longer than a login.
            // Known limit: a server that hangs after login still costs this default timeout. Add a per-account deadline if that shows up.
            client.Timeout = commandTimeout;
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            // MailKit blames the certificate when a server goes silent during the TLS handshake.
            if (ex is SslHandshakeException { InnerException: TimeoutException timeout })
                throw timeout;
            throw;
        }
    }

    internal static DateTime? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new McpException($"Invalid date for '{name}': '{value}'. Use yyyy-MM-dd.");
    }

    // Attachments alone misses files marked inline or not marked at all, as some mail programs send PDFs.
    internal static IEnumerable<MimeEntity> AttachmentParts(MimeMessage msg) =>
        msg.BodyParts.Where(p => p.IsAttachment || (p.ContentDisposition?.FileName ?? p.ContentType.Name) is not null);

    private static string AttachmentName(MimeEntity entity) => entity switch
    {
        MessagePart mp => (mp.ContentDisposition?.FileName ?? mp.ContentType.Name ?? "message") + ".eml",
        MimePart part when !string.IsNullOrWhiteSpace(part.FileName) => part.FileName,
        _ => "attachment",
    };

    private static bool BreaksLine(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    // Headers and names can hold encoded line breaks, which would forge extra output lines.
    internal static string OneLine(string text) => string.Concat(text.Select(c => BreaksLine(c) ? ' ' : c));

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // Windows drops trailing dots and spaces, which would turn ".." into the parent folder.
        var clean = new string(Path.GetFileName(name)
            .Select(c => BreaksLine(c) || invalid.Contains(c) ? '_' : c)
            .ToArray()).TrimEnd('.', ' ');

        if (clean.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(clean);
            if (extension.Length > 10)
                extension = "";
            var cut = MaxFileNameLength - extension.Length;
            if (char.IsHighSurrogate(clean[cut - 1]))
                cut--;
            clean = clean[..cut].TrimEnd('.', ' ') + extension;
        }

        if (clean.Length == 0)
            return "attachment";

        // Windows opens a device for these names, so the data would vanish while the save looks fine.
        return Regex.IsMatch(clean, "^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3]) *(\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "_" + clean
            : clean;
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
        return path;
    }

    // These tags start a new line. Any other tag sits inside a line and must not split a word or an amount.
    // Known limit: adjacent links and spans styled display:block still join up. Read the style attribute if that hurts.
    private static readonly HashSet<HtmlTagId> LineBreakTags =
    [
        HtmlTagId.Address, HtmlTagId.Article, HtmlTagId.Aside, HtmlTagId.BlockQuote, HtmlTagId.Body, HtmlTagId.Br, HtmlTagId.Caption,
        HtmlTagId.Center, HtmlTagId.DD, HtmlTagId.Details, HtmlTagId.Dir, HtmlTagId.Div, HtmlTagId.DL, HtmlTagId.DT, HtmlTagId.FieldSet,
        HtmlTagId.FigCaption, HtmlTagId.Figure, HtmlTagId.Footer, HtmlTagId.Form, HtmlTagId.H1, HtmlTagId.H2,
        HtmlTagId.H3, HtmlTagId.H4, HtmlTagId.H5, HtmlTagId.H6, HtmlTagId.Head, HtmlTagId.Header, HtmlTagId.HR, HtmlTagId.Html,
        HtmlTagId.Legend, HtmlTagId.LI,
        HtmlTagId.Main, HtmlTagId.Menu, HtmlTagId.Nav, HtmlTagId.OL, HtmlTagId.Option, HtmlTagId.P, HtmlTagId.Pre, HtmlTagId.Section,
        HtmlTagId.Summary, HtmlTagId.Table, HtmlTagId.TBody, HtmlTagId.Tfoot, HtmlTagId.THead, HtmlTagId.Title, HtmlTagId.TR, HtmlTagId.UL,
    ];

    // A tokenizer plus one pass stays linear on hostile mail. Regex does not: backtracking can hang, and
    // NonBacktracking kept about 1 GB per MB of input in its cache, then returned wrong matches for later mails.
    // Known limit: attribute spam costs about 60 times the HTML size in short-lived memory. Cap the HTML size if that ever hurts.
    internal static string? HtmlToText(string? html)
    {
        if (html is null)
            return null;

        var text = new StringBuilder();
        char? gap = null; // The separator owed before the next visible character.
        var hidden = false;
        // WebUtility below, because the tokenizer leaves references such as the common &#13; as literal text.
        var tokenizer = new HtmlTokenizer(new StringReader(html)) { DecodeCharacterReferences = false };
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                // The tokenizer returns the content of these as text, with any markup inside it left as is.
                case HtmlTagToken { Id: HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.NoScript or HtmlTagId.IFrame or HtmlTagId.NoEmbed or HtmlTagId.NoFrames } tag:
                    hidden = !tag.IsEndTag && !tag.IsEmptyElement;
                    break;
                case HtmlTagToken { Id: HtmlTagId.TD or HtmlTagId.TH or HtmlTagId.Image }:
                    gap ??= ' ';
                    break;
                case HtmlTagToken tag when LineBreakTags.Contains(tag.Id):
                    gap = '\n';
                    break;
                case HtmlDataToken data when !hidden:
                    foreach (var c in WebUtility.HtmlDecode(data.Data))
                    {
                        if (c is '\n' or '\r')
                        {
                            gap = '\n';
                        }
                        else if (char.IsWhiteSpace(c))
                        {
                            gap ??= ' ';
                        }
                        // Invisible filler after a gap is not a word: it must not use up maxChars.
                        else if ((gap is null && text.Length > 0) || char.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark))
                        {
                            if (gap is { } separator && text.Length > 0)
                                text.Append(separator);
                            gap = null;
                            text.Append(c);
                        }
                    }

                    break;
            }
        }

        return text.ToString();
    }
}
