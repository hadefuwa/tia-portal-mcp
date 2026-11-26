using System.Text;

namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// Siemens Openness wraps the real reason for a failure (for example why an import was refused)
/// in InnerException. The top-level message is only "Error when calling method 'Import' of type ...",
/// which hides the cause. This joins the whole chain so it reaches the tool caller.
/// </summary>
public static class ExceptionDetail
{
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = (e.Message ?? string.Empty).Trim();
            if (msg.Length == 0 || !seen.Add(msg)) continue;
            if (sb.Length > 0) sb.Append("\n  caused by: ");
            sb.Append(msg);
        }
        return sb.ToString();
    }

    /// <summary>Runs <paramref name="action"/>; any failure is rethrown with the full cause chain in its message.</summary>
    public static void Wrap(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // Describe() has the message chain; ToString() adds the exception types, Data entries and stack,
            // which is where Openness puts anything beyond the generic "Error when calling method" text.
            var sb = new StringBuilder($"{what}: {Describe(ex)}");
            sb.Append("\n  type: ").Append(ex.GetType().FullName);
            foreach (System.Collections.DictionaryEntry d in ex.Data)
                sb.Append("\n  data ").Append(d.Key).Append(" = ").Append(d.Value);
            for (var i = ex.InnerException; i is not null; i = i.InnerException)
                sb.Append("\n  inner type: ").Append(i.GetType().FullName);
            throw new InvalidOperationException(sb.ToString(), ex);
        }
    }
}
