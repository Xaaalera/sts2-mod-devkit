namespace Sts2.Devkit;

public static class AtomicJsonFile
{
    public static async Task WriteAsync(string destination, string json)
    {
        var temporary = destination + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
                File.Move(temporary, destination, true);
                return;
            }
            catch (Exception exception) when (attempt < 7 && IsSharingFailure(exception))
            {
                // Retrying identical publication bytes never replays the SDK operation.
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException && (exception.HResult & 0xffff) is 5 or 32 or 33;
}
