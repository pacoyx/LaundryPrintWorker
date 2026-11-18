using Microsoft.AspNetCore.SignalR.Client;

namespace LaundryPrintWorker.Services;

// Politica de reconexion automatica que nunca se rinde. El WithAutomaticReconnect() por
// defecto solo reintenta 4 veces (0s, 2s, 10s, 30s) y luego dispara Closed; en una mini PC
// que se suspende o pierde la red por varios minutos eso pasaba casi siempre, y cada Closed
// arrancaba un ciclo de reconexion manual nuevo. Reintentando para siempre, Closed pasa a
// ser raro (solo StopAsync explicito o un fallo no recuperable) y la reconexion la maneja
// SignalR sola.
public sealed class IndefiniteRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] InitialDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };

    private static readonly TimeSpan SteadyDelay = TimeSpan.FromSeconds(30);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var baseDelay = retryContext.PreviousRetryCount < InitialDelays.Length
            ? InitialDelays[retryContext.PreviousRetryCount]
            : SteadyDelay;

        if (baseDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Jitter de hasta +20% para que varios workers (o varias PCs que vuelven de un corte
        // de luz/red al mismo tiempo) no golpeen al servidor exactamente en el mismo instante.
        var jitterMs = Random.Shared.NextDouble() * baseDelay.TotalMilliseconds * 0.2;
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }
}
