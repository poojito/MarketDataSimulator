internal class Orderbook : IDisposable
{
    public Orderbook(Instrument instrument, IOrderbookService service)
    {
        _instrument = instrument;
        _spinTask = GenerateUpdatesAsync(new WeakReference<Orderbook>(this), instrument, service, _disposedSource);
    }

    private static async Task GenerateUpdatesAsync(WeakReference<Orderbook> orderbook,
        Instrument instrument,
        IOrderbookService service,
        TaskCompletionSource disposedSource)
    {
        var random = new Random(DateTime.Now.Millisecond);

        while (true)
        {
            if (!orderbook.TryGetTarget(out var model))
                return;

            try
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(random.NextDouble() + 2)),
                    disposedSource.Task).ConfigureAwait(false);

                if (disposedSource.Task.IsCompleted)
                    return;

                OrderbookUpdate update = null;

                if (random.NextDouble() > 0.95)
                {
                    var snapshot = model.Refresh(random, instrument);
                    update = new OrderbookUpdate(
                        new OrderbookSnapshotUpdate(instrument.Id, snapshot.Bids, snapshot.Asks));
                }
                else
                {
                    var incremental = model.Update(random, instrument);
                    update = new OrderbookUpdate(
                        new OrderbookIncrementalUpdate(instrument.Id, incremental.UpdateType, incremental.Level));
                }

                await service.OnOrderbookUpdateAsync(update).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to perform orderbook update: {ex}");
            }
            finally
            {
                model = null;
            }
        }
    }
}


// Orderbook Level Update
private OrderbookLevelUpdate Update(Random random, Instrument instrument)
{
    lock (_disposedLock)
    {
        if (_disposed)
            return OrderbookLevelUpdate.Empty;

        uint GetQuantity() => (uint)random.Next(1, 1000);
        int GetPrice() => random.Next(-100, 100);

        List<OrderbookLevel> levels = null;
        SortedSet<OrderbookLevel> oppositeSortedLevels = null;
        SortedSet<OrderbookLevel> sortedLevels = null;

        var buy = random.Next(0, 2) == 0;

        levels = buy ? _bidLevels : _askLevels;
        oppositeSortedLevels = buy ? _asks : _bids;
        sortedLevels = buy ? _bids : _asks;

        var replace = levels.Count == instrument.Specifications.Depth;
        var remove = random.NextDouble() < (levels.Count / (double)(instrument.Specifications.Depth + 1));
        var index = random.Next(0, levels.Count - 1);

        if (remove)
        {
            return RemoveLevel(index, levels, sortedLevels);
        }
        else if (replace)
        {
            return ReplaceLevel(index, levels, sortedLevels, GetQuantity());
        }
        else
        {
            return AddLevel(levels, oppositeSortedLevels, sortedLevels, buy, GetPrice(), GetQuantity());
        }
    }
}


// how to stream orderbook updates

public override async Task StreamOrderbookUpdates(IAsyncStreamReader<Subscription> requestStream, IServerStreamWriter<Proto.OrderbookUpdate> responseStream, ServerCallContext context)
{
    try
    {
        var client = new ServerClient(context.Peer, responseStream);

        lock (_clientsLock)
            _clients.Add(client.Host, client);

        Console.WriteLine($"Added client {client.Host}");

        try
        {
            var streamingCompletionSource = new TaskCompletionSource();
            using (var streamingCompleteTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
            using (var streamingCompleteRegistration = streamingCompleteTokenSource.Token.Register(() => streamingCompletionSource.SetResult()))
            using (var readClientSubscriptionRequestsTask = ReadClientSubscriptionRequestsAsync(requestStream, client, streamingCompleteTokenSource.Token))
            {
                await Task.WhenAny(streamingCompletionSource.Task, readClientSubscriptionRequestsTask).ConfigureAwait(false);

                if (streamingCompletionSource.Task.IsCompleted)
                    return;

                await readClientSubscriptionRequestsTask.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_clientsLock)
                _clients.Remove(client.Host);

            Console.WriteLine($"Removed client {client.Host}");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"Error in {nameof(StreamOrderbookUpdates)}: {e}");
    }
}


private async Task ProcessSubscribeRequestAsync(Subscription current, ServerClient client, CancellationToken token)
{
    var (addedSubscriptions, removedSubscriptions) = client.Update(
        current.Subscribe?.Ids.ToHashSet(),
        current.Unsubscribe?.Ids.ToHashSet());

    if (addedSubscriptions.Any())
        Console.WriteLine($"{client.Host} subscribed to {string.Join(",", addedSubscriptions)}");

    if (removedSubscriptions.Any())
        Console.WriteLine($"{client.Host} unsubscribed from {string.Join(",", removedSubscriptions)}");

    var removedOrderbooks = new List<OrderbookSnapshotUpdate>();
    var addedOrderbooks = new List<OrderbookSnapshotUpdate>();

    foreach (var removedSubscription in removedSubscriptions)
        removedOrderbooks.Add(new OrderbookSnapshotUpdate(removedSubscription));

    foreach (var addedSubscription in addedSubscriptions)
        addedOrderbooks.Add(_orderbookManager.GetSnapshot(addedSubscription));

    var removedOrderbookSendTask = Task.WhenAll(removedOrderbooks.Select(i => client.SendAsync(new OrderbookUpdate(i), default)));
    var addedOrderbookSendTask = Task.WhenAll(addedOrderbooks.Select(i => client.SendAsync(new OrderbookUpdate(i), default)));

    await Task.WhenAll(removedOrderbookSendTask, addedOrderbookSendTask).ConfigureAwait(false);
}




