using MdReader.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(AppLink.CreateDefault());
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReaderTools>();

await builder.Build().RunAsync();
return 0;
