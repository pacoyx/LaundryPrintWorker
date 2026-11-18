using LaundryPrintWorker.Models;
using LaundryPrintWorker.Services;
using Microsoft.AspNetCore.SignalR;
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

    // Serializa TODO intento de conexion/registro. Antes, el arranque, el handler de Closed y
    // el de Resume lanzaban cada uno su propio ciclo de StartAsync sobre la MISMA HubConnection;
    // cuando uno fallaba porque otro ya habia conectado, hacia StopAsync y cortaba la conexion
    // buena del otro -> Closed -> otro ciclo mas. Se multiplicaban hasta tener decenas de ciclos
    // en paralelo y el servidor recibia un RegisterPrinter cada 1-3 segundos. Con este semaforo
    // las llamadas concurrentes esperan su turno (no se descartan) y al entrar re-evaluan el
    // estado: si otro ya dejo todo conectado y registrado, retornan al instante.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // ConnectionId para el que ya se ejecuto RegisterPrinter con exito. Cada conexion nueva
    // (StartAsync o auto-reconnect) trae un ConnectionId distinto, asi que comparar contra
    // este valor dice si hace falta registrar o no, sin re-registrar la misma conexion.
    private volatile string? _registeredConnectionId;

    // Momento en que el servidor rechazo RegisterPrinter con HubException (error de negocio,
    // p. ej. WorkerKey invalida). Mientras no pase RejectedRegistrationRetryDelay, el loop de
    // heartbeat no reintenta el registro. Se guarda como ticks para leer/escribir de forma atomica.
    private long _registrationRejectedAtTicks;

    private DateTime? RegistrationRejectedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _registrationRejectedAtTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
        set => Interlocked.Exchange(ref _registrationRejectedAtTicks, value?.Ticks ?? 0);
    }

    private static readonly TimeSpan RejectedRegistrationRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan TransitionPollDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(30);

    // Si entre dos vueltas del loop de heartbeat paso mucho mas que el intervalo normal, el
    // equipo casi seguro estuvo suspendido/hibernado (el proceso no corrio en ese lapso).
    private static readonly TimeSpan SuspendGapThreshold = TimeSpan.FromMinutes(2);

    private const int ZombieHeartbeatFailures = 2;

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
            // impresion. Pasa por el punto unico: como el ConnectionId cambio, registra una vez.
            try
            {
                await EnsureConnectedAndRegisteredAsync("Reconnected", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // El servicio se esta deteniendo: nada que hacer.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo re-registrar el worker tras la reconexión de SignalR.");
            }
        };

        connection.Closed += async error =>
        {
            // Con la politica de reintento indefinida (IndefiniteRetryPolicy) esto deberia ser
            // raro: solo tras un StopAsync explicito (p. ej. al tratar una conexion zombie) o un
            // fallo no recuperable. Igual se maneja para que el servicio nunca quede "vivo" pero
            // sin conexion.
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning(
                error,
                "Conexión SignalR cerrada. Se intentará reconectar y registrar.");

            try
            {
                // Pequeña espera con jitter para no reconectar en el mismo instante en que se cayo.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 5000)), stoppingToken);
                await EnsureConnectedAndRegisteredAsync("Closed", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // El servicio se esta deteniendo: nada que hacer.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconectando tras el cierre de la conexión SignalR.");
            }
        };

        // Si Windows suspende el equipo (monitor+sistema, no solo el monitor), el socket de
        // SignalR muere y la reconexion automatica puede tardar en notarlo. Escuchar el evento
        // de resume del sistema operativo permite verificar la conexion y redescubrir impresoras
        // apenas la PC despierta. OJO: en un servicio de Windows SystemEvents.PowerModeChanged
        // puede no dispararse nunca; por eso el loop de heartbeat tambien detecta el "salto" de
        // tiempo tras una suspension y ejecuta la misma rutina (ver ExecuteAsync mas abajo).
        _powerModeHandler = async (sender, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            {
                return;
            }

            await HandleResumeAsync("PowerModeChanged.Resume", stoppingToken);
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

        await EnsureConnectedAndRegisteredAsync("Arranque del servicio", stoppingToken);

        try
        {
            await DiscoverPrintersAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error sincronizando impresoras. El worker sigue activo igual.");
        }

        var consecutiveHeartbeatFailures = 0;
        string? lastFailedConnectionId = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Deteccion de suspension independiente de SystemEvents (que puede no dispararse en
            // un servicio): se mide SOLO lo que dura el Task.Delay. Si "durmio" mucho mas que el
            // intervalo, el equipo estuvo suspendido/hibernado. Medir solo el Delay evita falsos
            // positivos cuando la vuelta anterior tardo por un reintento de conexion largo.
            var beforeDelayUtc = DateTime.UtcNow;
            await Task.Delay(HeartbeatInterval, stoppingToken);
            var gap = DateTime.UtcNow - beforeDelayUtc;

            if (gap > SuspendGapThreshold)
            {
                await HandleResumeAsync(
                    $"Salto de tiempo de {gap.TotalMinutes:F1} min en el loop de heartbeat (posible suspensión)",
                    stoppingToken);
                consecutiveHeartbeatFailures = 0;
                lastFailedConnectionId = null;
                continue;
            }

            try
            {
                switch (connection.State)
                {
                    case HubConnectionState.Disconnected:
                        // Red de seguridad: nadie esta intentando conectar (o el intento anterior
                        // termino) y la conexion quedo caida.
                        consecutiveHeartbeatFailures = 0;
                        lastFailedConnectionId = null;
                        await EnsureConnectedAndRegisteredAsync("Heartbeat: conexión en estado Disconnected", stoppingToken);
                        break;

                    case HubConnectionState.Connecting:
                    case HubConnectionState.Reconnecting:
                        // El auto-reconnect (o un StartAsync en curso) se encarga; no interferir.
                        // La conexion que fallaba ya no existe: el contador de zombie no aplica.
                        consecutiveHeartbeatFailures = 0;
                        lastFailedConnectionId = null;
                        _logger.LogDebug("Heartbeat omitido: conexión en estado {State}.", connection.State);
                        break;

                    case HubConnectionState.Connected:
                        var heartbeatConnectionId = connection.ConnectionId;
                        var heartbeat = await TrySendHeartbeatAsync(connection, stoppingToken);
                        if (heartbeat.Alive)
                        {
                            consecutiveHeartbeatFailures = 0;
                            lastFailedConnectionId = null;

                            // Solo avisar si de verdad se va a re-registrar: durante el backoff por
                            // registro rechazado el servidor siempre dira "no vigente" y el aviso
                            // cada 30 s seria ruido engañoso (el LogCritical del rechazo ya lo explica).
                            if (heartbeat.IsCurrent == false && CanRetryRejectedRegistration())
                            {
                                _logger.LogWarning(
                                    "El servidor indica que esta conexión no es la vigente del worker (ConnectionId: {ConnectionId}). Se re-registrará.",
                                    heartbeatConnectionId);
                                _registeredConnectionId = null;
                            }

                            // Conectado pero sin registrar para esta conexion (p. ej. fallo el
                            // RegisterPrinter del handler de Reconnected, o el servidor dice que
                            // no es la vigente): completar el registro, respetando el backoff si
                            // el servidor rechazo el registro (WorkerKey invalida, etc.).
                            if (heartbeatConnectionId is not null
                                && heartbeatConnectionId != _registeredConnectionId
                                && CanRetryRejectedRegistration())
                            {
                                await EnsureConnectedAndRegisteredAsync("Heartbeat: conectado sin registrar", stoppingToken);
                            }
                        }
                        else
                        {
                            // Solo cuentan fallos consecutivos sobre la MISMA conexion.
                            if (heartbeatConnectionId != lastFailedConnectionId)
                            {
                                consecutiveHeartbeatFailures = 0;
                                lastFailedConnectionId = heartbeatConnectionId;
                            }

                            consecutiveHeartbeatFailures++;
                            if (consecutiveHeartbeatFailures >= ZombieHeartbeatFailures)
                            {
                                consecutiveHeartbeatFailures = 0;
                                lastFailedConnectionId = null;
                                await ForceReconnectAsync(
                                    $"Heartbeat: {ZombieHeartbeatFailures} fallos consecutivos estando Connected (conexión zombie)",
                                    heartbeatConnectionId,
                                    stoppingToken);
                            }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el loop de heartbeat");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_powerModeHandler is not null)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerModeHandler;
            _powerModeHandler = null;
        }

        await base.StopAsync(cancellationToken);

        // Cierre limpio de la conexion: asi el servidor recibe el cierre y limpia la conexion
        // (grupo del worker, registro) de inmediato, en vez de esperar a su ClientTimeout.
        // base.StopAsync ya cancelo stoppingToken, por lo que el handler de Closed no reconecta.
        try
        {
            await _signalR.Connection.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error cerrando la conexión SignalR al detener el servicio (ignorado).");
        }
    }

    // Punto de entrada UNICO para conectar/registrar en el Hub. Lo usan el arranque, Closed,
    // Reconnected, el resume de suspension y la red de seguridad del heartbeat. Serializado con
    // _connectGate: nunca hay dos ciclos de StartAsync a la vez sobre la misma HubConnection.
    private async Task EnsureConnectedAndRegisteredAsync(string reason, CancellationToken stoppingToken)
    {
        await _connectGate.WaitAsync(stoppingToken);
        try
        {
            await EnsureConnectedAndRegisteredCoreAsync(reason, stoppingToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // Trata una conexion "zombie" (Connected segun SignalR, pero el servidor no responde):
    // la detiene UNA vez y luego reconecta por el camino normal. Se hace dentro del semaforo y
    // solo si la conexion sigue siendo la misma que fallo (staleConnectionId), para no cortar
    // una conexion nueva y sana que otro camino ya haya levantado mientras tanto.
    private async Task ForceReconnectAsync(string reason, string? staleConnectionId, CancellationToken stoppingToken)
    {
        var connection = _signalR.Connection;

        await _connectGate.WaitAsync(stoppingToken);
        try
        {
            if (connection.State == HubConnectionState.Connected && connection.ConnectionId == staleConnectionId)
            {
                _logger.LogWarning(
                    "Deteniendo conexión SignalR zombie (ConnectionId: {ConnectionId}). Motivo: {Reason}",
                    staleConnectionId,
                    reason);

                try
                {
                    await connection.StopAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Ignorado: vamos a reconectar de todas formas.
                    _logger.LogDebug(ex, "Error deteniendo la conexión zombie (ignorado).");
                }

                _registeredConnectionId = null;
            }
            else
            {
                _logger.LogInformation(
                    "Conexión zombie ya reemplazada (estado {State}); no se detiene nada. Motivo: {Reason}",
                    connection.State,
                    reason);
            }

            await EnsureConnectedAndRegisteredCoreAsync(reason, stoppingToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // Reintenta la conexion/registro en el Hub indefinidamente en vez de tumbar el servicio
    // (BackgroundService.ExecuteAsync lanzando una excepcion mata todo el host). Asi el worker
    // se recupera solo cuando vuelve el internet. DEBE llamarse con _connectGate tomado.
    private async Task EnsureConnectedAndRegisteredCoreAsync(string reason, CancellationToken stoppingToken)
    {
        var connection = _signalR.Connection;
        var attempt = 0;
        HubConnectionState? lastWaitingState = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var state = connection.State;
            var connectionId = connection.ConnectionId;

            if (state == HubConnectionState.Connected && connectionId is not null && connectionId == _registeredConnectionId)
            {
                if (attempt == 0)
                {
                    _logger.LogInformation(
                        "Conexión/registro omitido: ya conectado y registrado (ConnectionId: {ConnectionId}). Motivo: {Reason}",
                        connectionId,
                        reason);
                }

                return;
            }

            attempt++;

            try
            {
                switch (state)
                {
                    case HubConnectionState.Disconnected:
                        _logger.LogInformation(
                            "Conectando al Hub (intento {Attempt}). Motivo: {Reason}",
                            attempt,
                            reason);

                        _registeredConnectionId = null;
                        await connection.StartAsync(stoppingToken);

                        _logger.LogInformation(
                            "Conectado al Hub. ConnectionId: {ConnectionId}. Motivo: {Reason}",
                            connection.ConnectionId,
                            reason);
                        // Volver a evaluar: el siguiente paso (Connected) registra.
                        continue;

                    case HubConnectionState.Connecting:
                    case HubConnectionState.Reconnecting:
                        // No llamar StartAsync ni StopAsync: dejar trabajar al auto-reconnect (o al
                        // StartAsync en curso). Si se rinde, llegara Closed y el estado pasara a
                        // Disconnected, que este mismo loop atiende.
                        if (lastWaitingState != state)
                        {
                            _logger.LogInformation(
                                "Conexión en estado {State}; esperando a que termine antes de registrar. Motivo: {Reason}",
                                state,
                                reason);
                            lastWaitingState = state;
                        }

                        await Task.Delay(TransitionPollDelay, stoppingToken);
                        continue;

                    case HubConnectionState.Connected:
                        lastWaitingState = null;

                        _logger.LogInformation(
                            "Registrando worker en el Hub (ConnectionId: {ConnectionId}, intento {Attempt}). Motivo: {Reason}",
                            connectionId,
                            attempt,
                            reason);

                        // Timeout propio: en una conexion zombie el InvokeAsync podria quedar
                        // colgado hasta que salte el ServerTimeout; asi el reintento es acotado.
                        try
                        {
                            using var registerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            registerCts.CancelAfter(RegisterTimeout);
                            await connection.InvokeAsync("RegisterPrinter", _options.WorkerKey, Environment.MachineName, registerCts.Token);
                        }
                        catch (HubException ex)
                        {
                            // El servidor RESPONDIO con un error de negocio (WorkerKey invalida,
                            // worker deshabilitado, etc.): reintentar cada 15s no lo va a arreglar y
                            // retendria _connectGate para siempre, bloqueando el arranque (descubrimiento
                            // de impresoras y heartbeat). Se sale liberando el semaforo; el loop de
                            // heartbeat lo reintenta con backoff largo (RejectedRegistrationRetryDelay)
                            // mientras siga "Connected sin registrar".
                            RegistrationRejectedAtUtc = DateTime.UtcNow;
                            _logger.LogCritical(
                                ex,
                                "El servidor rechazó el registro del worker (ConnectionId: {ConnectionId}). Revise la WorkerKey. Se reintentará en {Minutes} min. Motivo: {Reason}",
                                connectionId,
                                RejectedRegistrationRetryDelay.TotalMinutes,
                                reason);
                            return;
                        }

                        // Se guarda el ConnectionId capturado ANTES del invoke: si la conexion
                        // cambio en el medio, el handler de Reconnected registrara la nueva.
                        _registeredConnectionId = connectionId;
                        RegistrationRejectedAtUtc = null;

                        _logger.LogInformation(
                            "Worker registrado correctamente (ConnectionId: {ConnectionId}). Motivo: {Reason}",
                            connectionId,
                            reason);

                        if (connectionId is null)
                        {
                            // Sin ConnectionId no hay con que comparar; dar el registro por hecho.
                            return;
                        }

                        continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // IMPORTANTE: aqui NO se hace StopAsync. Si la conexion esta Connected (p. ej. fallo
                // solo el RegisterPrinter) se reintenta el registro sobre la misma conexion; si
                // esta Connecting/Reconnecting la maneja el auto-reconnect; si quedo Disconnected
                // el siguiente intento hace StartAsync. Detenerla aqui era lo que cortaba la
                // conexion buena de otro ciclo y multiplicaba las reconexiones.
                _logger.LogError(
                    ex,
                    "No fue posible conectar/registrar al Hub (estado {State}, intento {Attempt}). Motivo: {Reason}. Reintentando en {Delay} segundos...",
                    connection.State,
                    attempt,
                    reason,
                    RetryDelay.TotalSeconds);

                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    // Rutina tras reanudar de suspension (por SystemEvents o por el salto de tiempo detectado
    // en el loop de heartbeat). Si la conexion responde al Heartbeat no se corta nada; si no
    // responde, se trata como zombie. Nunca lanza excepciones (se llama desde un async void).
    private async Task HandleResumeAsync(string reason, CancellationToken stoppingToken)
    {
        var connection = _signalR.Connection;

        try
        {
            _logger.LogWarning("Equipo reanudado de suspensión ({Reason}). Verificando conexión al Hub.", reason);

            var state = connection.State;
            var connectionId = connection.ConnectionId;

            var heartbeat = state == HubConnectionState.Connected
                ? await TrySendHeartbeatAsync(connection, stoppingToken)
                : default;

            if (heartbeat.Alive)
            {
                _logger.LogInformation(
                    "Conexión al Hub sana tras reanudar (ConnectionId: {ConnectionId}); no se reconecta.",
                    connectionId);

                if (heartbeat.IsCurrent == false)
                {
                    _logger.LogWarning(
                        "El servidor indica que esta conexión no es la vigente del worker (ConnectionId: {ConnectionId}). Se re-registrará.",
                        connectionId);
                    _registeredConnectionId = null;
                }

                // Barato si ya esta registrado (retorna al instante); cubre el caso de una
                // conexion sana pero sin registrar.
                await EnsureConnectedAndRegisteredAsync($"Resume ({reason})", stoppingToken);
            }
            else if (state == HubConnectionState.Connected)
            {
                // Connected segun SignalR pero no responde: zombie. Se detiene UNA vez (intencional).
                await ForceReconnectAsync($"Resume ({reason}): Heartbeat sin respuesta", connectionId, stoppingToken);
            }
            else
            {
                await EnsureConnectedAndRegisteredAsync($"Resume ({reason}): estado {state}", stoppingToken);
            }

            await DiscoverPrintersAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // El servicio se esta deteniendo: nada que hacer.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconectando tras reanudar de suspensión.");
        }
    }

    // True si se puede intentar registrar: no hubo rechazo del servidor, o ya paso el backoff.
    private bool CanRetryRejectedRegistration()
    {
        var rejectedAt = RegistrationRejectedAtUtc;
        return rejectedAt is null || DateTime.UtcNow - rejectedAt.Value >= RejectedRegistrationRetryDelay;
    }

    // Resultado de un Heartbeat. Alive: el servidor respondio. IsCurrent: lo que devolvio el
    // servidor (true = esta conexion es la vigente registrada del worker; false = no lo es;
    // null = backend viejo cuyo Heartbeat es void, o respondio con error -> no se sabe).
    private readonly record struct HeartbeatResult(bool Alive, bool? IsCurrent);

    // Envia un Heartbeat con timeout propio.
    private async Task<HeartbeatResult> TrySendHeartbeatAsync(HubConnection connection, CancellationToken stoppingToken)
    {
        try
        {
            // Timeout explicito ademas del propio de SignalR: una conexion "zombie" (TCP medio
            // abierto, tipico de redes caseras/NAT inestable) puede dejar este InvokeAsync
            // esperando mas de lo normal. Con el linked token, a los 10s se cancela esta
            // espera puntual (no mata la conexion entera).
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            heartbeatCts.CancelAfter(HeartbeatTimeout);

            // Se pide bool? (no bool): contra un backend viejo con Heartbeat void, el mensaje de
            // completion llega sin resultado y el cliente entrega null (con bool no-nullable el
            // unboxing de null lanzaria). null se trata como OK, sin re-registrar.
            var isCurrent = await connection.InvokeAsync<bool?>("Heartbeat", heartbeatCts.Token);
            _logger.LogDebug("Heartbeat enviado. Vigente={IsCurrent}", isCurrent);
            return new HeartbeatResult(true, isCurrent);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HubException ex)
        {
            // El servidor respondio (con un error del metodo): la conexion esta viva, no es zombie.
            _logger.LogWarning(ex, "El Hub respondió con error al Heartbeat (la conexión está viva).");
            return new HeartbeatResult(true, null);
        }
        catch (Exception ex) when (ex is InvalidCastException or NullReferenceException)
        {
            // Defensa: si alguna version del cliente no pudiera convertir la respuesta de un
            // Heartbeat void a bool?, el servidor igual respondio -> conexion viva, sin re-registrar.
            _logger.LogDebug(ex, "Respuesta de Heartbeat no convertible a bool?; se trata como OK.");
            return new HeartbeatResult(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando Heartbeat");
            return new HeartbeatResult(false, null);
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
