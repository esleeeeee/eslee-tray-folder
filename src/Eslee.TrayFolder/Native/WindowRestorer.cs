using System.Diagnostics;

namespace Eslee.TrayFolder.Native;

public sealed class WindowRestorer
{
    private const int MaximumAttempts = 6;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public async Task<bool> TryRestoreAsync(Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint handle;
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return false;
                }

                handle = process.MainWindowHandle;
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (handle != nint.Zero)
            {
                NativeMethods.ShowWindowAsync(
                    handle,
                    NativeMethods.IsIconic(handle) ? NativeMethods.SwRestore : NativeMethods.SwShow);
                NativeMethods.BringWindowToTop(handle);
                NativeMethods.SetForegroundWindow(handle);
                return true;
            }

            if (attempt + 1 < MaximumAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
