using MdReader.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0)
{
    // Both usage paths return before any configuration code runs.
    if (args[0] != "setup")
    {
        Console.Error.WriteLine(
            "MdReader.Bridge is an MCP server started by Claude. To register it: MdReader.Bridge.exe setup [--remove]");
        return 2;
    }
    if (args.Length > 2 || (args.Length == 2 && args[1] != "--remove"))
    {
        Console.Error.WriteLine("Usage: MdReader.Bridge.exe setup [--remove]");
        return 2;
    }
    return SetupCommand.Run(remove: args.Length == 2);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(AppLink.CreateDefault());
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReaderTools>();

await builder.Build().RunAsync();
return 0;
