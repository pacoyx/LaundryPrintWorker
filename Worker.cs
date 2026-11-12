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

    private Microsoft.Win32.PowerModeChangedEventHandler? _powerModeHandler;

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

        connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation(
                "SignalR reconectado. ConnectionId: {ConnectionId}",
                connectionId);

            // El auto-reconnect de SignalR abre una conexion nueva (nuevo ConnectionId): el
            // servidor ya no tiene a este worker en el grupo "worker-{id}" (eso solo se arma
            // dentro de RegisterPrinter, ver PrintHub.cs), asi que sin este re-registro el
            // worker queda "conectado" pero invisible para el despacho de trabajos de
            // impresion -ningun error visible, simplemente deja de recibir PrintAvailable
            // hasta que alguien reinicia el servicio a mano.
            try
            {
                await connection.InvokeAsync("RegisterPrinter", _options.WorkerKey, Environment.MachineName, stoppingToken);
                _logger.LogInformation("Worker re-registrado tras la reconexión.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo re-registrar el worker tras la reconexión de SignalR.");
            }
        };

        connection.Closed += async error =>
        {
            // WithAutomaticReconnect() solo reintenta unas pocas veces (0s, 2s, 10s, 30s) y
            // luego se rinde para siempre si no logra reconectar -tipicamente porque la PC
            // estuvo suspendida/sin red mas tiempo del que duran esos reintentos. Sin este
            // manejador, el servicio queda "vivo" pero inutil silenciosamente: no se cae,
            // pero jamas vuelve a registrar impresoras ni recibir trabajos de impresion.
            _logger.LogError(
                error,
                "Conexión SignalR cerrada definitivamente. Reintentando registro desde cero...");

            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 5)), stoppingToken);
            await ConnectAndRegisterWithRetryAsync(connection, stoppingToken);
        };

        // Si Windows suspende el equipo (monitor+sistema, no solo el monitor), el socket de
        // SignalR muere y la reconexion automatica puede tardar en notarlo o directamente
        // rendirse (ver manejador de Closed arriba). Escuchar el evento de resume del sistema
        // operativo permite forzar la reconexion y el redescubrimiento de impresoras apenas la
        // PC despierta, en vez de esperar a que un heartbeat fallido lo detecte.
        _powerModeHandler = async (sender, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            {
                return;
            }

            try
            {
                _logger.LogWarning("Equipo reanudado de suspensión. Forzando reconexión al Hub.");

                if (connection.State != HubConnectionState.Disconnected)
                {
                    try
                    {
                        await connection.StopAsync(stoppingToken);
                    }
                    catch
                    {
                        // Ignorado: vamos a reconectar de todas formas.
                    }
                }

                await ConnectAndRegisterWithRetryAsync(connection, stoppingToken);
                await DiscoverPrintersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconectando tras reanudar de suspensión.");
            }
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += _powerModeHandler;

        connection.On<PrintNotificationDto>("PrintAvailable",
            async notification =>
            {
                _logger.LogDebug("Trabajo recibido. QueueId={QueueId}, PrinterId={PrinterId}, PrinterName={PrinterName}", notification.QueueId, notification.PrinterId, notification.PrinterName);
                await ProcessPrintJob(notification.QueueId,notification.PrinterName,  stoppingToken);
            });

        connection.On<TestPrintCommandDto>("PrintTestTicket",
            async command =>
            {
                _logger.LogDebug("Prueba de impresión recibida. PrinterName={PrinterName}", command.PrinterName);
                try
                {
                    await _printerService.PrintTestAsync(command.PrinterName, command.Timestamp);
                    _logger.LogDebug("Prueba de impresión completada en '{Printer}'", command.PrinterName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en la prueba de impresión en '{Printer}'", command.PrinterName);
                }
            });

        await ConnectAndRegisterWithRetryAsync(connection, stoppingToken);

        try
        {
            await DiscoverPrintersAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sincronizando impresoras. El worker sigue activo igual.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Timeout explicito ademas del propio de SignalR: una conexion "zombie" (TCP medio
                // abierto, tipico de redes caseras/NAT inestable) puede dejar este InvokeAsync
                // esperando mas de lo normal. Con el linked token, a los 10s se cancela esta
                // espera puntual (no mata la conexion entera) y el loop sigue su curso normal.
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                heartbeatCts.CancelAfter(TimeSpan.FromSeconds(10));
                await connection.InvokeAsync("Heartbeat", heartbeatCts.Token);
                _logger.LogDebug("Heartbeat enviado");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando Heartbeat");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_powerModeHandler is not null)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerModeHandler;
            _powerModeHandler = null;
        }

        return base.StopAsync(cancellationToken);
    }

    // Reintenta la conexion/registro en el Hub indefinidamente en vez de tumbar
    // el servicio (BackgroundService.ExecuteAsync lanzando una excepcion mata
    // todo el host). Asi el worker se recupera solo cuando vuelve el internet,
    // sin que alguien tenga que reiniciar el servicio a mano.
    private async Task ConnectAndRegisterWithRetryAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                _logger.LogInformation("Conectado al Hub.");

                await connection.InvokeAsync("RegisterPrinter", _options.WorkerKey, Environment.MachineName, stoppingToken);
                _logger.LogInformation("Worker registrado correctamente.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No fue posible conectar/registrar al Hub. Reintentando en 15 segundos...");

                if (connection.State != HubConnectionState.Disconnected)
                {
                    try
                    {
                        await connection.StopAsync(stoppingToken);
                    }
                    catch
                    {
                        // Ignorado: ya estamos manejando un error de conexion, no queremos
                        // que un fallo al detener tape el error original.
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task ProcessPrintJob(long queueId, string printerName, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Procesando trabajo {QueueId}", queueId);
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
            _logger.LogDebug("Trabajo {QueueId} completado", queueId);
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