using LaundryPrintWorker;
using LaundryPrintWorker.Models;
using LaundryPrintWorker.Services;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<PrintWorkerOptions>(
    builder.Configuration.GetSection(
        "WorkerSettings"));

builder.Services.Configure<TicketSettings>(
    builder.Configuration.GetSection(
        "TicketSettings"));

builder.Services.AddHttpClient<PrintApiClient>(
    (sp, client) =>
    {
        var options =
            sp.GetRequiredService<
                IOptions<PrintWorkerOptions>>();

        client.BaseAddress =
            new Uri(
                options.Value.ApiBaseUrl);
    });

builder.Services.AddSingleton<
    SignalRConnectionService>();

builder.Services.AddSingleton<
    TicketPrinterService>();

builder.Services.AddHostedService<Worker>();

builder.Services.AddWindowsService();

var host = builder.Build();

host.Run();
