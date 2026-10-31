using System.Runtime.CompilerServices;

namespace ContextWave.Tests;

internal static class TestEnvironment
{
    /// <summary>Keeps test runs out of the user's real Context Wave folder (settings, logs, voices).</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var home = Directory.CreateTempSubdirectory("contextwave-tests-").FullName;
        Environment.SetEnvironmentVariable("MDREADER_HOME", home);
    }
}
