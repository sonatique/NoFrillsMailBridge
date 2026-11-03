using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using ModelContextProtocol.Server;

namespace MailBridge;

[McpServerToolType]
public static class MailTools
{
    private const string AccountHelp = "Account name, a comma separated list of names, or 'all'.";

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
            if (TryDate(since) is { } s)
                query = query.And(SearchQuery.DeliveredAfter(s));
            if (TryDate(before) is { } b)
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
                    var files = string.Join(", ", m.Attachments.Select(x => x.FileName ?? x.ContentType.Name));
                    sb.AppendLine($"uid={m.UniqueId.Id} | {date} | {m.Envelope?.From} | {m.Envelope?.Subject}" +
                                  (files.Length > 0 ? $" | attachments: {files}" : ""));
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
        using var client = await ConnectAsync(a, ct);
        var box = await client.GetFolderAsync(folder, ct);
        await box.OpenAsync(FolderAccess.ReadOnly, ct);
        var msg = await box.GetMessageAsync(new UniqueId(uid), ct);
        await client.DisconnectAsync(true, ct);

        var body = msg.TextBody ?? HtmlToText(msg.HtmlBody) ?? "";
        if (body.Length > maxChars)
            body = body[..maxChars] + "\n[truncated]";

        var files = msg.Attachments.Select(AttachmentName).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Account: {a.Name}");
        sb.AppendLine($"From: {msg.From}");
        sb.AppendLine($"To: {msg.To}");
        if (msg.Cc.Count > 0)
            sb.AppendLine($"Cc: {msg.Cc}");
        sb.AppendLine($"Date: {msg.Date:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Subject: {msg.Subject}");
        sb.AppendLine($"Attachments: {(files.Count == 0 ? "none" : string.Join(", ", files))}");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
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
        using var client = await ConnectAsync(a, ct);
        var box = await client.GetFolderAsync(folder, ct);
        await box.OpenAsync(FolderAccess.ReadOnly, ct);
        var msg = await box.GetMessageAsync(new UniqueId(uid), ct);
        await client.DisconnectAsync(true, ct);

        var dir = Path.Combine(saveDir, a.Name, $"{msg.Date:yyyyMMdd}_uid{uid}");
        Directory.CreateDirectory(dir);

        var saved = new List<string>();
        foreach (var entity in msg.Attachments)
        {
            var path = UniquePath(dir, SafeName(AttachmentName(entity)));
            await using var stream = File.Create(path);

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

            saved.Add(path);
        }

        return saved.Count == 0 ? "No attachments." : string.Join('\n', saved);
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

    private static async Task<ImapClient> ConnectAsync(AccountConfig account, CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(account.Host, account.Port, account.SocketOptions, ct);
        await client.AuthenticateAsync(account.User, account.ResolvePassword(), ct);
        return client;
    }

    private static DateTime? TryDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string AttachmentName(MimeEntity entity) => entity switch
    {
        MessagePart mp => (mp.ContentDisposition?.FileName ?? "message") + ".eml",
        MimePart part => part.FileName ?? part.ContentType.Name ?? "attachment",
        _ => "attachment",
    };

    private static string SafeName(string name)
    {
        var clean = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return clean.Length == 0 ? "attachment" : clean;
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
        return path;
    }

    private static string? HtmlToText(string? html)
    {
        if (html is null)
            return null;
        var noBlocks = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ");
        var lines = Regex.Replace(noBlocks, "(?i)<br\\s*/?>|</p>|</div>|</tr>", "\n");
        var plain = WebUtility.HtmlDecode(Regex.Replace(lines, "<[^>]+>", " "));
        return Regex.Replace(plain, "[ \\t]+", " ").Trim();
    }
}
