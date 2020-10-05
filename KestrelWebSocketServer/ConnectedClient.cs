using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KestrelWebSocketServer
{
    public class ConnectedClient
    {
        Stopwatch watch = new Stopwatch();
        private int messagesReceived = 0;
        private Timer _timer;
        private long lastMessageSerial = 0;
        private long lastAckSerial = 0;

        public ConnectedClient(int socketId, WebSocket socket, TaskCompletionSource<object> taskCompletion)
        {
            watch.Start();
            SocketId = socketId;
            Socket = socket;
            TaskCompletion = taskCompletion;
            _timer = new Timer(Callback, null, 1000, 1000);
        }

        private void Callback(object? state)
        {
            Console.WriteLine("Messages received: " + messagesReceived + " at rate: " + messagesReceived / ((watch.ElapsedMilliseconds + 1) / 1000.0) + " m/s");
            var lastSerial = Volatile.Read(ref lastMessageSerial);
            if (lastSerial != lastAckSerial)
            {
                BroadcastQueue.Add(CreateAckMessage(lastSerial));
                lastAckSerial = lastSerial;
            }
        }

        public int SocketId { get; private set; }

        public WebSocket Socket { get; private set; }

        public TaskCompletionSource<object> TaskCompletion { get; private set; }

        public BlockingCollection<string> BroadcastQueue { get; } = new BlockingCollection<string>();
        public BlockingCollection<string> ReceiveQueue { get; } = new BlockingCollection<string>();

        public CancellationTokenSource BroadcastLoopTokenSource { get; set; } = new CancellationTokenSource();
        public CancellationTokenSource ReceiveLoopTokenSource { get; set; } = new CancellationTokenSource();

        public async Task BroadcastLoopAsync()
        {
            var cancellationToken = BroadcastLoopTokenSource.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    //await Task.Delay(Program.BROADCAST_TRANSMIT_INTERVAL_MS, cancellationToken);
                    if (!cancellationToken.IsCancellationRequested && Socket.State == WebSocketState.Open &&
                        BroadcastQueue.TryTake(out var message))
                    {
                        //Console.WriteLine($"Socket {SocketId}: Sending from queue.");
                        var msgbuf = new ArraySegment<byte>(Encoding.UTF8.GetBytes(message));
                        await Socket.SendAsync(msgbuf, WebSocketMessageType.Text, endOfMessage: true,
                            CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal upon task/token cancellation, disregard
                }
                catch (Exception ex)
                {
                    Program.ReportException(ex);
                }
            }
        }

        public async Task ReceiveLoopAsync()
        {
            var cancellationToken = ReceiveLoopTokenSource.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!cancellationToken.IsCancellationRequested && Socket.State == WebSocketState.Open &&
                        ReceiveQueue.TryTake(out var message))
                    {
                        var p = ConvertToProtocolMessage(message);
                        if (p != null)
                        {
                            if (p.Action == ProtocolMessage.MessageAction.Attach)
                            {
                                BroadcastQueue.Add(CreateAttachMessage(p.Channel));
                            }
                            else if (p.Action == ProtocolMessage.MessageAction.Message && p.AckRequired)
                            {
                                Volatile.Write(ref lastMessageSerial, p.MsgSerial);
                                Interlocked.Increment(ref messagesReceived);
                            } else if (p.Action == ProtocolMessage.MessageAction.Message)
                            {
                                Interlocked.Increment(ref messagesReceived);
                            }
                        }

                        //Console.WriteLine($"Received message: {message}");
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal upon task/token cancellation, disregard
                }
                catch (Exception ex)
                {
                    Program.ReportException(ex);
                }
            }
        }

        public ProtocolMessage ConvertToProtocolMessage(string message)
        {
            try
            {
                return JsonConvert.DeserializeObject<ProtocolMessage>(message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to deserialize message. Error: " + e.Message);
                throw;
            }
        }

        public string CreateAckMessage(long serial)
        {
            var result = JObject.FromObject(new
            {
                action = 1,
                count = 1,
                msgSerial = serial
            });
            return result.ToString();
        }

        public string CreateAttachMessage(string channelName)
        {
            var result = JObject.FromObject(new
            {
                action = 11,
                flags = 983040,
                channel = channelName,
                channelSerial = "108F_woMQApszW35535298:1123"
            });
            return result.ToString();
        }
    }

    public class ProtocolMessage
    {
        /// <summary>
        /// Action associated with the message.
        /// </summary>
        public enum MessageAction
        {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable SA1602 // Enumeration items should be documented
            Heartbeat = 0,
            Ack = 1,
            Nack = 2,
            Connect = 3,
            Connected = 4,
            Disconnect = 5,
            Disconnected = 6,
            Close = 7,
            Closed = 8,
            Error = 9,
            Attach = 10,
            Attached = 11,
            Detach = 12,
            Detached = 13,
            Presence = 14,
            Message = 15,
            Sync = 16,
            Auth = 17
#pragma warning restore SA1602 // Enumeration items should be documented
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        }

        /// <summary>
        /// Message Flag sent by the server.
        /// </summary>
        [Flags]
        public enum Flag
        {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable SA1602 // Enumeration items should be documented
            HasPresence = 1 << 0,
            HasBacklog = 1 << 1,
            Resumed = 1 << 2,
            HasLocalPresence = 1 << 3,
            Transient = 1 << 4,
            AttachResume = 1 << 5,

            // Channel modes
            Presence = 1 << 16,
            Publish = 1 << 17,
            Subscribe = 1 << 18,
            PresenceSubscribe = 1 << 19,
#pragma warning restore SA1602 // Enumeration items should be documented
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        }

        /// <summary>
        /// Helper method to check for the existence of a flag in an integer.
        /// </summary>
        /// <param name="value">int value storing the flag.</param>
        /// <param name="flag">flag we check for.</param>
        /// <returns>true or false.</returns>
        public static bool HasFlag(int? value, Flag flag)
        {
            if (value == null)
            {
                return false;
            }

            return (value.Value & (int) flag) != 0;
        }

        /// <summary>
        /// Channel params is a Dictionary&lt;string, string&gt; which is used to pass parameters to the server when
        /// attaching a channel. Some params include `delta` and `rewind`. The server will also echo the params in the
        /// ATTACHED message.
        /// For more information https://www.ably.io/documentation/realtime/channel-params.
        /// </summary>
        [JsonProperty("params")]
        public JObject Params { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProtocolMessage"/> class.
        /// </summary>
        public ProtocolMessage()
        {
        }

        internal ProtocolMessage(MessageAction action)
            : this()
        {
            Action = action;
        }

        internal ProtocolMessage(MessageAction action, string channel)
            : this(action)
        {
            Channel = channel;
        }

        /// <summary>
        /// Current message action.
        /// </summary>
        [JsonProperty("action")]
        public MessageAction Action { get; set; }

        /// <summary>
        /// <see cref="AuthDetails"/> for the current message.
        /// </summary>
        [JsonProperty("auth")]
        public JObject Auth { get; set; }

        /// <summary>
        /// Current message flags.
        /// </summary>
        [JsonProperty("flags")]
        public int? Flags { get; set; }

        /// <summary>
        /// Count.
        /// </summary>
        [JsonProperty("count")]
        public int? Count { get; set; }

        /// <summary>
        /// Error associated with the message.
        /// </summary>
        [JsonProperty("error")]
        public JObject Error { get; set; }

        /// <summary>
        /// Ably generated message id.
        /// </summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>
        /// Optional channel for which the message belongs to.
        /// </summary>
        [JsonProperty("channel")]
        public string Channel { get; set; }

        /// <summary>
        /// Current channel serial.
        /// </summary>
        [JsonProperty("channelSerial")]
        public string ChannelSerial { get; set; }

        /// <summary>
        /// Current connectionId.
        /// </summary>
        [JsonProperty("connectionId")]
        public string ConnectionId { get; set; }

        /// <summary>
        /// Current connection serial.
        /// </summary>
        [JsonProperty("connectionSerial")]
        public long? ConnectionSerial { get; set; }

        /// <summary>
        /// Current message serial.
        /// </summary>
        [JsonProperty("msgSerial")]
        public long MsgSerial { get; set; }

        /// <summary>
        /// Timestamp of the message.
        /// </summary>
        [JsonProperty("timestamp")]
        public DateTimeOffset? Timestamp { get; set; }

        /// <summary>
        /// Connection details received. <see cref="IO.Ably.ConnectionDetails"/>.
        /// </summary>
        [JsonProperty("connectionDetails")]
        public JObject ConnectionDetails { get; set; }

        [JsonIgnore] internal bool AckRequired => Action == MessageAction.Message || Action == MessageAction.Presence;

        /// <inheritdoc/>
        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append("{ ")
                .AppendFormat("action={0}", Action);

            text.Append(" }");
            return text.ToString();
        }
    }
}