using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmuku.Import;

public sealed class ImportWorker(ImportService imports, MediaBindingService bindings, ISqliteConnectionFactory factory,
    StorageInitializationState initialization, TimeProvider clock, ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await initialization.Ready.WaitAsync(stoppingToken).ConfigureAwait(false)) return;
        var nextSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (clock.GetUtcNow() >= nextSweep)
                {
                    await imports.SweepAsync(stoppingToken).ConfigureAwait(false);
                    nextSweep = clock.GetUtcNow().AddSeconds(60);
                }
                bool pending;
                using (var c = factory.CreateOpenConnection())
                    pending = ImportSql.Long(c, null, "SELECT COUNT(*) FROM ImportTasks WHERE Status='Queued' AND Stage IN ('Received','Ready')") > 0;
                if (pending) await imports.ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
                await bindings.ProcessCheckJobAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Danmuku import maintenance failed; durable state is retained for retry."); }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
        }
    }
}
