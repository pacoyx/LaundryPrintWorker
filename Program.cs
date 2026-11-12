using LaundryPrintWorker;
using LaundryPrintWorker.Models;
using LaundryPrintWorker.Services;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var seqSettings = new SeqSettings();
builder.Configuration.GetSection("Seq").Bind(seqSettings);

// Logging a archivo local (para ver que paso sin depender de que la red/Seq esten disponibles)
// y a Seq (visibilidad centralizada, mismo servidor que ya usa el backend). El Event Log de
// Windows que AddWindowsService() configura mas abajo sigue funcionando igual -esto se agrega,
// no lo reemplaza.
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Los handlers por defecto de IHttpClientFactory loguean 4 lineas en Information por cada
    // llamada HTTP saliente (inicio/fin x logical+client handler). Entre eso y nuestros propios
    // logs de progreso, un solo trabajo de impresion generaba ~13 lineas de ruido. Silenciado a
    // Warning: solo se ve algo de HttpClient si de verdad falla una llamada.
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "LaundryPrintWorker")
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14);

if (!string.IsNullOrWhiteSpace(seqSettings.Url))
{
    loggerConfig = loggerConfig.WriteTo.Seq(seqSettings.Url, apiKey: string.IsNullOrWhiteSpace(seqSettings.ApiKey) ? null : seqSettings.ApiKey);
}

Log.Logger = loggerConfig.CreateLogger();

builder.Logging.AddSerilog(Log.Logger, dispose: true);

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
        client.Timeout = TimeSpan.FromSeconds(30);
    });

builder.Services.AddSingleton<
    SignalRConnectionService>();

builder.Services.AddSingleton<
    TicketPrinterService>();

builder.Services.AddHostedService<Worker>();

builder.Services.AddWindowsService();

var host = builder.Build();

host.Run();
