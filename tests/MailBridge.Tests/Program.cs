// Self-check of the rules that handle input from strangers. Any failure throws, so the exit code is not zero.
using System.Text;
using MailBridge;
using MimeKit;
using ModelContextProtocol;

static void Check(bool ok, string what)
{
    if (!ok)
        throw new Exception("FAILED: " + what);
}

static void Same(string? actual, string expected) =>
    Check(actual == expected, $"expected [{expected}], got [{actual}]");

// Dates: only yyyy-MM-dd, never a silently dropped filter.
Check(MailTools.ParseDate(null, "since") is null && MailTools.ParseDate("  ", "since") is null, "empty date means no filter");
Check(MailTools.ParseDate("2026-11-03", "since") == new DateTime(2026, 11, 3), "valid date");
foreach (var bad in new[] { "last week", "2026-13-45", "03/11/2026", "2026-11-03T10:00" })
{
    try
    {
        MailTools.ParseDate(bad, "since");
        Check(false, $"date '{bad}' must be rejected");
    }
    catch (McpException)
    {
    }
}

// File names: nothing outside the folder, no device, no over-long name, no line break.
foreach (var empty in new[] { "", " ", ".", "..", "...", "foo/.." })
    Same(MailTools.SafeName(empty), "attachment");
Same(MailTools.SafeName("../../evil.txt"), "evil.txt");
Same(MailTools.SafeName("report.pdf. "), "report.pdf");
Same(MailTools.SafeName("a\nb\u2028c.txt"), "a_b_c.txt");
foreach (var device in new[] { "NUL", "nul.txt", "NUL .txt", "CON", "COM1.pdf", "LPT9", "CONOUT$", "COM\u00B9" })
    Same(MailTools.SafeName(device), "_" + device);
foreach (var fine in new[] { "NULL.txt", "console.log", "COM10.txt", "invoice.pdf", ".hidden" })
    Same(MailTools.SafeName(fine), fine);
foreach (var longName in new[] { new string('x', 300) + ".txt", "a." + new string('x', 300), new string('\u4E2D', 300) + ".txt" })
{
    var safe = MailTools.SafeName(longName);
    Check(safe.Length <= 80 && Encoding.UTF8.GetByteCount(safe) <= 250, $"long name cut: {safe.Length} chars");
}
Check(MailTools.SafeName(new string('x', 300) + ".txt").EndsWith(".txt"), "long name keeps its extension");
Check(!char.IsSurrogate(MailTools.SafeName(new string('x', 79) + "\U0001F600\U0001F600")[^1]), "cut does not split a character");

// HTML: real text survives, amounts stay whole, words stay apart.
Same(MailTools.HtmlToText("<p>Total: CHF <b>1</b><b>000</b>.<i>50</i></p>"), "Total: CHF 1000.50");
Same(MailTools.HtmlToText("<table><tr><td>Qty</td><td>Price</td></tr><tr><td>2</td><td>9.50</td></tr></table>"), "Qty Price\n2 9.50");
Same(MailTools.HtmlToText("<table><tr><td>Qty<td>Price<tr><td>2<td>9.50</table>"), "Qty Price\n2 9.50");
Same(MailTools.HtmlToText("<div dir=\"ltr\">Hello Bob,<div>Invoice 12345.</div><div>Total 500<br class=\"\">Thanks</div></div>"), "Hello Bob,\nInvoice 12345.\nTotal 500\nThanks");
Same(MailTools.HtmlToText("<p>Hello<p>World<hr>Footer"), "Hello\nWorld\nFooter");
Same(MailTools.HtmlToText("<style>p{}</style><SCRIPT type=x>alert(1)</script >Hello &amp; &lt;b&gt;<br/>Next"), "Hello & <b>\nNext");
Same(MailTools.HtmlToText("<style>a</style>keep<style>b</style>"), "keep");
Same(MailTools.HtmlToText("a < b, 1<2 <!-- hidden > text -->end"), "a < b, 1<2 end");
Same(MailTools.HtmlToText("  Hello&nbsp;&nbsp;\t world  "), "Hello world");
Same(MailTools.HtmlToText("<html><head><title>T</title></head><body>x"), "T\nx");
Same(MailTools.HtmlToText("a&#13;b&amp;lt;c"), "a\nb&lt;c");

Same(MailTools.HtmlToText("<p>One</p><style/><p>Two</p><style>x</style><p>Three</p><script/>Four"), "One\nTwo\nThree\nFour");
Same(MailTools.HtmlToText("<noscript><img src=x><p>y</p></noscript>Hello<iframe>z</iframe> bye"), "Hello bye");
Same(MailTools.HtmlToText("Thanks<img src=e.png>and bye"), "Thanks and bye");

// HTML: filler must not use up maxChars.
var filler = new[] { "<tr><td>\r\n\r\n</td></tr>\r\n", "<div>&zwnj;</div>", "<div>&#847;</div>", "&nbsp;", "&#13;" };
foreach (var unit in filler)
    Same(MailTools.HtmlToText("<p>Hello</p>" + string.Concat(Enumerable.Repeat(unit, 5000)) + "<p>Order 12345</p>")?.Length.ToString(), "Hello\nOrder 12345".Length.ToString());

// HTML: hostile input stays fast and small, and leaves no damage behind for the next mail.
var longRun = new string('a', 1024 * 1024);
var hostile = new[] { "<", " ", "<style>", "<p ", "<a b=", "&", "&#", "</td>", "<br>", "<!--", "<![CDATA[", "\n", "&nbsp;" }
    .Select(unit => string.Concat(Enumerable.Repeat(unit, 1024 * 1024 / unit.Length)))
    .Concat(new[] { "<" + longRun + ">", "<a b=\"" + longRun, "<!--" + longRun, "<style>" + longRun });
foreach (var input in hostile)
{
    var before = GC.GetTotalAllocatedBytes(true);
    Check(Task.Run(() => MailTools.HtmlToText(input)).Wait(TimeSpan.FromSeconds(20)), $"hostile input starting [{input[..8]}] must not hang");
    var megabytes = (GC.GetTotalAllocatedBytes(true) - before) / (1024 * 1024);
    Check(megabytes < 300, $"hostile input starting [{input[..8]}] allocated {megabytes} MB");
    Same(MailTools.HtmlToText("<span>Amount due: 5</span>"), "Amount due: 5");
    Same(MailTools.HtmlToText("a<b>c</b>."), "ac.");
}

// Headers: no character may start a new output line.
Same(MailTools.OneLine("a\vb\nc\r\nd" + (char)0x85 + "e" + (char)0x2028 + "f" + (char)0x1C + "g"), "a b c  d e f g");

// Attachments: inline and unmarked files count, the text body does not.
const string Mail = """
    From: a@example.com
    MIME-Version: 1.0
    Content-Type: multipart/mixed; boundary="b"

    --b
    Content-Type: text/plain

    Body
    --b
    Content-Type: application/pdf; name="inline.pdf"
    Content-Disposition: inline; filename="inline.pdf"

    x
    --b
    Content-Type: application/pdf; name="unmarked.pdf"

    x
    --b
    Content-Type: application/pdf
    Content-Disposition: attachment; filename="marked.pdf"

    x
    --b
    Content-Type: message/rfc822; name="forward.eml"
    Content-Disposition: inline; filename="forward.eml"

    Subject: inner

    hello
    --b--
    """;
var message = MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(Mail)));
Same(string.Join(",", MailTools.AttachmentParts(message).Select(p => p.ContentDisposition?.FileName ?? p.ContentType.Name)), "inline.pdf,unmarked.pdf,marked.pdf,forward.eml");

Console.WriteLine("All checks passed.");
