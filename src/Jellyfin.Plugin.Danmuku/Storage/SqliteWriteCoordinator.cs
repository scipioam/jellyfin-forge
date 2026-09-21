using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="ISqliteWriteCoordinator" />
public sealed class SqliteWriteCoordinator : ISqliteWriteCoordinator, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly Channel<IWriteOperation> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerLoop;
    private bool _disposed;

    public SqliteWriteCoordinator(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _queue = Channel.CreateUnbounded<IWriteOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerLoop = Task.Run(ProcessQueueAsync);
    }

    public Task<TResult> EnqueueAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var operation = new WriteOperation<TResult>(work, cancellationToken);
        if (!_queue.Writer.TryWrite(operation))
        {
            throw new InvalidOperationException("The Danmuku SQLite write coordinator is no longer accepting work.");
        }

        return operation.Completion;
    }

    public async Task EnqueueAsync(
        Func<SqliteConnection, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await EnqueueAsync(
            async (connection, token) =>
            {
                await work(connection, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        await _writerLoop.ConfigureAwait(false);
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Synchronous fallback for containers that dispose singletons synchronously.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _writerLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested while draining queued work.
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var operation))
                {
                    try
                    {
                        using var connection = _connectionFactory.CreateOpenConnection();
                        await operation.ExecuteAsync(connection, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        operation.SetException(exception);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; queued work drains before cancellation in normal disposal.
        }
    }

    private interface IWriteOperation
    {
        Task ExecuteAsync(SqliteConnection connection, CancellationToken cancellationToken);

        void SetException(Exception exception);
    }

    private sealed class WriteOperation<TResult> : IWriteOperation
    {
        private readonly Func<SqliteConnection, CancellationToken, Task<TResult>> _work;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<TResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WriteOperation(
            Func<SqliteConnection, CancellationToken, Task<TResult>> work,
            CancellationToken cancellationToken)
        {
            _work = work;
            _cancellationToken = cancellationToken;
        }

        public Task<TResult> Completion => _completion.Task;

        public async Task ExecuteAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
            try
            {
                var result = await _work(connection, linked.Token).ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void SetException(Exception exception) => _completion.TrySetException(exception);
    }
}
