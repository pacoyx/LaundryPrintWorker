using LaundryPrintWorker.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace LaundryPrintWorker.Services;

public class SignalRConnectionService
{
    private readonly HubConnection _connection;

    public SignalRConnectionService(
        IOptions<PrintWorkerOptions> options)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(
                options.Value.SignalRHubUrl)
            // Reconexion automatica indefinida (ver IndefiniteRetryPolicy): evita que SignalR
            // se rinda y dispare Closed tras unos pocos intentos.
            .WithAutomaticReconnect(new IndefiniteRetryPolicy())
            .Build();

        // Timeouts explicitos, iguales a los defaults, para dejar documentado que deben ser
        // coherentes con el servidor (AddSignalR() por defecto: KeepAlive 15s, ClientTimeout
        // 30s). ServerTimeout debe ser al menos el doble del KeepAlive del servidor; bajarlo
        // provocaria cortes falsos. Es este timeout el que detecta conexiones "zombie" (TCP
        // medio abierto) y pasa la conexion a Reconnecting.
        _connection.ServerTimeout = TimeSpan.FromSeconds(30);
        _connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _connection.HandshakeTimeout = TimeSpan.FromSeconds(15);
    }

    public HubConnection Connection => _connection;
}
