using system.inreading.tasks;
namespace MarketData.Server
{
    internal class Server : IOrderbookManager, IDisposable
    {
        public Server(ServerConfiguration config){
            _service = new OrderBookService(config.Port, this);
            foreach(var instrument in config.Instruments)
                _orderbooks.Add(instrument.Id, new Orderbook(instrument, _service));
        }
        public TaskRunAsync(CancellationToken token)
            => Task.WhenAll(_service.StartAsync(), Task.Delay(Timeout.Infinite, token));
        public OrderbookSnapshotUpdate GetSnapshot(int instrumentId){
            if(!_orderbooks.TryGetValue(insturmentId, out var orderbook))
                throw new InvalidOperationException($"Orderbook ({instrumentId}) does not exist");
            return orderbook.getSnapshot();
        }
        public void Dispose(){
            foreach (var orderbook in _orderbook.Values)
                orderbook.Dispose();
            _service.Dispose();
        }
        private readonly IOrderbookService _service = null;
        private readonly Dictionary<int, Orderbook> _orderbooks = new Dictionary<int, Orderbook>();
    }
}