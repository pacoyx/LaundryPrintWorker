using System.Net.Http.Json;
using LaundryPrintWorker.Models;

namespace LaundryPrintWorker.Services;


public class PrintApiClient
{
    private readonly HttpClient _httpClient;

    public PrintApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PrintJobDto?> GetPrintJobAsync(long queueId, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<PrintJobDto>($"api/printjobs/{queueId}", cancellationToken);
    }

    public async Task MarkAsPrintedAsync(long queueId, CancellationToken cancellationToken)
    {
        await _httpClient.PostAsync($"api/printjobs/{queueId}/completed", null, cancellationToken);
    }
    
    public async Task MarkAsErrorAsync(long queueId, string message, CancellationToken cancellationToken)
    {
        await _httpClient.PostAsJsonAsync($"api/printjobs/{queueId}/error",
            new
            {
                ErrorMessage = message
            },
            cancellationToken);
    }
    
    public async Task DiscoverPrintersAsync(object request, CancellationToken cancellationToken)
    {
        await _httpClient.PostAsJsonAsync("api/workers/discovery", request, cancellationToken);
    }
}