using System;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using static SocketSoakClient.Logging;

namespace SocketSoakClient
{
    internal struct MessageToSend
    {
        public ArraySegment<byte> Message { get; }

        public WebSocketMessageType Type { get; }

        public MessageToSend(byte[] message, WebSocketMessageType type)
        {
            Message = new ArraySegment<byte>(message);
            Type = type;
        }
    }

    internal class MsWebSocketConnection : IDisposable
    {
        public enum ConnectionState
        {
            Connecting,
            Connected,
            Error,
            Closing,
            Closed
        }

        private bool _disposed = false;

        private readonly Uri _uri;
        private Action<ConnectionState, Exception> _handler;
        public WebSocketState State => ClientWebSocket.State;

        private readonly Channel<MessageToSend> _sendChannel = Channel.CreateUnbounded<MessageToSend>(
            new UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = false
            });

        internal ClientWebSocket ClientWebSocket { get; set; }

        private CancellationTokenSource _tokenSource = new CancellationTokenSource();

        public MsWebSocketConnection(Uri uri)
        {
            _uri = uri;
            ClientWebSocket = new ClientWebSocket();
        }

        public void SubscribeToEvents(Action<ConnectionState, Exception> handler) => _handler = handler;

        public async Task StartConnectionAsync()
        {
            _tokenSource = new CancellationTokenSource();
            _handler?.Invoke(ConnectionState.Connecting, null);
            try
            {
                await ClientWebSocket.ConnectAsync(_uri, CancellationToken.None).ConfigureAwait(false);
                StartSenderBackgroundThread();
                _handler?.Invoke(ConnectionState.Connected, null);
            }
            catch (Exception ex)
            {
                Error("Error connecting: " + ex.Message);

                _handler?.Invoke(ConnectionState.Error, ex);
            }
        }

        private void StartSenderBackgroundThread()
        {
            _ = Task.Factory.StartNew(_ => ProcessSenderQueue(), TaskCreationOptions.LongRunning, _tokenSource.Token);
        }

        private async Task ProcessSenderQueue()
        {
            if (_disposed)
            {
                throw new Exception(
                    $"Attempting to start sender queue consumer when {typeof(MsWebSocketConnection)} has been disposed is not allowed.");
            }

            try
            {
                while (await _sendChannel.Reader.WaitToReadAsync())
                {
                    while (_sendChannel.Reader.TryRead(out var message))
                    {
                        await Send(message.Message, message.Type, _tokenSource.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (ObjectDisposedException e)
            {
                Debug(
                    _disposed
                        ? $"{typeof(MsWebSocketConnection)} has been Disposed."
                        : "WebSocket Send operation cancelled.");
            }
            catch (OperationCanceledException e)
            {
                Debug(
                    _disposed
                        ? $"{typeof(MsWebSocketConnection)} has been Disposed, WebSocket send operation cancelled."
                        : "WebSocket Send operation cancelled.");
            }
            catch (Exception e)
            {
                Error("Error Sending to WebSocket. Error: " + e.Message);
                _handler?.Invoke(ConnectionState.Error, e);
            }
        }

        public async Task StopConnectionAsync()
        {
            _handler?.Invoke(ConnectionState.Closing, null);
            try
            {
                if (ClientWebSocket.CloseStatus.HasValue)
                {
                    Debug("Closing websocket. Close status: "
                          + Enum.GetName(typeof(WebSocketCloseStatus), ClientWebSocket.CloseStatus)
                          + ", Description: " + ClientWebSocket.CloseStatusDescription);
                }

                if (!_disposed)
                {
                    if (ClientWebSocket?.State != WebSocketState.Closed)
                    {
                        await ClientWebSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            string.Empty,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    _tokenSource?.Cancel();
                }

                _handler?.Invoke(ConnectionState.Closed, null);
            }
            catch (ObjectDisposedException ex)
            {
                Debug($"Error stopping connection. {typeof(MsWebSocketConnection)} was disposed.");

                _handler?.Invoke(ConnectionState.Closed, ex);
            }
            catch (Exception ex)
            {
                Error("Error stopping connection. " + ex.Message);
                _handler?.Invoke(ConnectionState.Closed, ex);
            }
        }

        public void SendText(string message)
        {
            EnqueueForSending(new MessageToSend(message.GetBytes(), WebSocketMessageType.Text));
        }

        public void SendData(byte[] data)
        {
            EnqueueForSending(new MessageToSend(data, WebSocketMessageType.Binary));
        }

        private void EnqueueForSending(MessageToSend message)
        {
            try
            {
                var writeResult = _sendChannel.Writer.TryWrite(message);
                if (writeResult == false)
                {
                    Error("Failed to enqueue message to WebSocket connection");
                }
            }
            catch (Exception e)
            {
                var msg = _disposed
                    ? $"EnqueueForSending failed. {typeof(MsWebSocketConnection)} has been Disposed."
                    : "EnqueueForSending failed.";

                Error(msg + "Error: " + e.Message);
                throw;
            }
        }

        private async Task Send(ArraySegment<byte> data, WebSocketMessageType type, CancellationToken token)
        {
            if (ClientWebSocket.State != WebSocketState.Open)
            {
                Error(
                    $"Trying to send message of type {type} when the socket is {ClientWebSocket.State}. Ack for this message will fail shortly.");
                return;
            }

            await ClientWebSocket.SendAsync(data, type, true, token).ConfigureAwait(false);
        }

        public async Task Receive(Action<string> handleMessage)
        {
            while (ClientWebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var buffer = new ArraySegment<byte>(new byte[1024 * 16]); // Default receive buffer size
                    WebSocketReceiveResult result = null;
                    using (var ms = new MemoryStream())
                    {
                        do
                        {
                            result = await ClientWebSocket.ReceiveAsync(buffer, _tokenSource.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                break;
                            }

                            ms.Write(buffer.Array ?? throw new InvalidOperationException("buffer cannot be null"),
                                buffer.Offset, result.Count);
                        } while (!result.EndOfMessage);

                        ms.Seek(0, SeekOrigin.Begin);

                        switch (result.MessageType)
                        {
                            case WebSocketMessageType.Text:
                                var text = ms.ToArray().GetText();
                                handleMessage?.Invoke(text);
                                break;
                            case WebSocketMessageType.Binary:
                                throw new Exception("Binary not supported");
                                break;
                        }
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await StopConnectionAsync();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _handler?.Invoke(ConnectionState.Error, ex);
                    break;
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _tokenSource.Cancel();
                _tokenSource.Dispose();
                _sendChannel?.Writer.Complete();
                ClientWebSocket?.Dispose();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);

            // Typically we would call GC.SuppressFinalize(this)
            // at this point in the Dispose pattern
            // to suppress an expensive GC cycle
            // But disposing of the Connection should not be frequent
            // and based on profiling this speeds up the release of objects
            // and reduces memory bloat considerably
        }

        /// <summary>
        /// Attempt to query the backlog length of the queue.
        /// </summary>
        /// <param name="count">The (approximate) count of items in the Channel.</param>
        public bool TryGetCount(out int count)
        {
            // get this using the reflection
            try
            {
                var prop = _sendChannel.GetType()
                    .GetProperty("ItemsCountForDebugger", BindingFlags.Instance | BindingFlags.NonPublic);
                if (prop != null)
                {
                    count = (int) prop.GetValue(_sendChannel);
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            count = default(int);
            return false;
        }

        ~MsWebSocketConnection()
        {
            Dispose(false);
        }
    }
}