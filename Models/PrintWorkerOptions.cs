namespace LaundryPrintWorker.Models;

public class PrintWorkerOptions
{
    public string WorkerKey { get; set; } = string.Empty;

    public string SignalRHubUrl { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = string.Empty;
}