using ContextWave.Bridge;

namespace ContextWave.Tests;

public class SetupCommandTests
{
    [Fact]
    public void Commands_never_run_programs_from_the_folder_setup_was_started_in()
    {
        var start = SetupCommand.CommandStart("claude mcp list");

        // cmd.exe by full path, so a cmd.exe in the current folder is not picked up.
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName, ignoreCase: true);
        // Run from the app's own folder, and told not to search the current folder for where or claude.
        Assert.Equal(AppContext.BaseDirectory, start.WorkingDirectory);
        Assert.Equal("1", start.Environment["NoDefaultCurrentDirectoryInExePath"]);
        Assert.Equal("/c claude mcp list", start.Arguments);
    }
}
