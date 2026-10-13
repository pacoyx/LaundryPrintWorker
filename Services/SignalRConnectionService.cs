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
            .WithAutomaticReconnect()
            .Build();
    }

    public HubConnection Connection => _connection;
}