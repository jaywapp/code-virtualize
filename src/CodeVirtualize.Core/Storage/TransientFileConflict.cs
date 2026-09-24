namespace CodeVirtualize.Core.Storage;

// External processes (such as on-access scanners) and concurrent readers briefly hold freshly
// written metadata files open. An overwrite rename or open that collides with such a holder fails
// with ACCESS_DENIED, ERROR_SHARING_VIOLATION or ERROR_UNABLE_TO_REMOVE_REPLACED; each of these
// leaves both files under their original names, so retrying is safe. Any other failure, or the
// last attempt, propagates unchanged. Total backoff is bounded at about 280 ms.
internal static class TransientFileConflict
{
    private const int MaxAttempts = 8;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorUnableToRemoveReplaced = 1175;
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(10);

    public static void Retry(Action operation) =>
        Retry(() =>
        {
            operation();
            return true;
        });

    public static T Retry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransient(exception))
            {
                Thread.Sleep(Backoff * attempt);
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is IOException or UnauthorizedAccessException &&
        (exception.HResult == Win32HResult(ErrorAccessDenied) ||
         exception.HResult == Win32HResult(ErrorSharingViolation) ||
         exception.HResult == Win32HResult(ErrorUnableToRemoveReplaced));

    private static int Win32HResult(int errorCode) => unchecked((int)0x80070000) | errorCode;
}
