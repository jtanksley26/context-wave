using System.Diagnostics;

namespace ContextWave.Tests;

internal static class TestUtil
{
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
