using System.Runtime.CompilerServices;

namespace MdReader.Tests;

internal static class TestEnvironment
{
    /// <summary>Keeps test runs out of the user's real MD Reader folder (settings, logs, voices).</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var home = Directory.CreateTempSubdirectory("mdreader-tests-").FullName;
        Environment.SetEnvironmentVariable("MDREADER_HOME", home);
    }
}
