using MdReader.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "setup")
    return SetupCommand.Run(remove: args.Contains("--remove"));

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(AppLink.CreateDefault());
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReaderTools>();

await builder.Build().RunAsync();
return 0;
