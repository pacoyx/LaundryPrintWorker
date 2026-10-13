using LaundryPrintWorker.Models;
using LaundryPrintWorker.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using System.Drawing.Printing;

public sealed class Worker : BackgroundService
{
    private readonly SignalRConnectionService _signalR;

    private readonly PrintApiClient _apiClient;

    private readonly TicketPrinterService _printerService;

    private readonly PrintWorkerOptions _options;

    private readonly ILogger<Worker> _logger;

    public Worker(
        SignalRConnectionService signalR,
        PrintApiClient apiClient,
        TicketPrinterService printerService,
        IOptions<PrintWorkerOptions> options,
        ILogger<Worker> logger)
    {
        _signalR = signalR;
        _apiClient = apiClient;
        _printerService = printerService;
        _options = options.Value;
        _logger = logger;
    }

    private static readonly string[] IgnoredPrinters =
    {
        "Microsoft Print to PDF",
        "Microsoft XPS Document Writer",
        "Fax",
        "OneNote"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = _signalR.Connection;

        connection.Reconnecting += error =>
        {
            _logger.LogWarning(
                error,
                "Reconectando SignalR...");

            return Task.CompletedTask;
        };

        connection.Reconnected += connectionId =>
        {
            _logger.LogInformation(
                "SignalR reconectado. ConnectionId: {ConnectionId}",
                connectionId);

            return Task.CompletedTask;
        };

        connection.Closed += error =>
        {
            _logger.LogError(
                error,
                "Conexión SignalR cerrada.");

            return Task.CompletedTask;
        };

        connection.On<PrintNotificationDto>("PrintAvailable",
            async notification =>
            {
                _logger.LogInformation("Trabajo recibido. QueueId={QueueId}, PrinterId={PrinterId}, PrinterName={PrinterName}", notification.QueueId, notification.PrinterId, notification.PrinterName);
                await ProcessPrintJob(notification.QueueId,notification.PrinterName,  stoppingToken);
            });
        try
        {
            await connection.StartAsync(stoppingToken);
            _logger.LogInformation("Conectado al Hub.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No fue posible conectar al Hub.");
            throw;
        }

        try
        {
            await connection.InvokeAsync("RegisterPrinter", _options.WorkerKey, Environment.MachineName, stoppingToken);
            _logger.LogInformation("Worker registrado correctamente.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registrando Worker.");
            throw;
        }

        await DiscoverPrintersAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.InvokeAsync("Heartbeat", stoppingToken);
                _logger.LogDebug("Heartbeat enviado");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando Heartbeat");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessPrintJob(long queueId, string printerName, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Procesando trabajo {QueueId}", queueId);
            var job = await _apiClient.GetPrintJobAsync(queueId, cancellationToken);
            if (job is null)
            {
                _logger.LogWarning("No se encontró trabajo {QueueId}", queueId);
                return;
            }
            
            //formateamos el ticket y lo imprimimos
            await _printerService.PrintAsync(job,printerName);
            
            //marcamos la cola de impresion como completado
            await _apiClient.MarkAsPrintedAsync(queueId, cancellationToken);
            _logger.LogInformation("Trabajo {QueueId} completado", queueId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando trabajo {QueueId}", queueId);
            try
            {
                // marcamos la cola de impresion como error
                await _apiClient.MarkAsErrorAsync(queueId, ex.Message, cancellationToken);
            }
            catch
            {
                // Evitar que un error de actualización
                // oculte el error original.
            }
        }
    }

    private async Task DiscoverPrintersAsync(CancellationToken cancellationToken)
    {
        var printers =
            PrinterSettings
                .InstalledPrinters
                .Cast<string>()
                .Where(p =>
                    !IgnoredPrinters.Any(i =>
                        p.Contains(
                            i,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

        var request = new
        {
            WorkerKey = _options.WorkerKey,

            ComputerName =
                Environment.MachineName,

            Printers = printers
        };

        await _apiClient.DiscoverPrintersAsync(
            request,
            cancellationToken);

        _logger.LogInformation(
            "Se sincronizaron {Count} impresoras",
            printers.Count);
    }
}