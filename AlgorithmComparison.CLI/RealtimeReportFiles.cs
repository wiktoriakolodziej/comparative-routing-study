using System.Text;

namespace AlgorithmComparison.CLI;

internal static class RealtimeReportFiles
{
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellation = default)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellation);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task AppendAsync(IReadOnlyList<(string Path, string Header, string Content)> entries,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var files = new List<(FileStream Stream, long Length, string Header, string Content)>();
        var errors = new List<Exception>();
        try
        {
            foreach (var entry in entries)
            {
                var stream = new FileStream(entry.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.Asynchronous);
                files.Add((stream, stream.Length, entry.Header, entry.Content));
            }
            foreach (var file in files)
            {
                file.Stream.Position = file.Length;
                var text = (file.Length == 0 && file.Header.Length > 0 ? file.Header + Environment.NewLine : "") + file.Content;
                await file.Stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation);
                await file.Stream.FlushAsync(cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            errors.Add(original);
            foreach (var file in files)
            {
                try { file.Stream.SetLength(file.Length); file.Stream.Flush(); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
        }
        finally
        {
            foreach (var file in files)
            {
                try { file.Stream.Dispose(); }
                catch (Exception closeError) { errors.Add(closeError); }
            }
        }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Report append, rollback or closing failed.", errors);
    }
}

public static class RealtimeSessionFinalizer
{
    public static async Task<bool> TryFinishAsync(Func<Task> finish, string status, string? primaryError, TextWriter errors)
    {
        try { await finish(); return true; }
        catch (Exception reportError)
        {
            if (primaryError is not null) errors.WriteLine($"Pierwotny błąd sesji: {primaryError}");
            errors.WriteLine($"Nie udało się zamknąć dziennika sesji (status: {status}): {reportError}");
            return false;
        }
    }
}
