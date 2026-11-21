using System.Security.Principal;
using ContextWave.Core;

namespace ContextWave.Tests;

public class AppPathsTests
{
    [Fact]
    public void Data_folder_and_pipe_name_stay_where_earlier_versions_put_them()
    {
        // Installs made before the rename keep their settings, voices and running app under these names.
        Assert.Equal("MDREADER_HOME", AppPaths.HomeVariable);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader"),
            AppPaths.RootFor(null));
        Assert.Equal(AppPaths.RootFor(null), AppPaths.RootFor(""));
        Assert.Equal(@"C:\elsewhere", AppPaths.RootFor(@"C:\elsewhere"));
        Assert.Equal($"MdReader.{WindowsIdentity.GetCurrent().User!.Value}", PipeProtocol.DefaultPipeName);
    }
}
