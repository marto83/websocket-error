using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static SocketSoakClient.Logging;

namespace SocketSoakClient
{
    public static class Logging
    {
        public static void Debug(string message) => ConsoleColor.Gray.WriteLine(message);
        public static void Error(string message) => ConsoleColor.Red.WriteLine(message);
        public static void Info(string message) => ConsoleColor.Green.WriteLine(message);
    }

    internal class Program
    {
        public static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            using var connection = Connect();

            var payload = JObject.Parse(File.ReadAllText("jsonPayload.json"));
            var msg = JObject.FromObject(new {action = 15, messages = new[] {payload}, msgSerial = 0 });

            int count = 0;

            while (true)
            {
                if (connection.State == WebSocketState.Closed)
                {
                    Info("Websocket closed");
                    return;
                }

                if (connection.State != WebSocketState.Open)
                {
                    Info("Waiting for an open connection");
                    await Task.Delay(50);
                    continue;
                }

                for (int i = 0; i < 2; i++)
                {
                    msg["msgSerial"] = count;
                    connection.SendText(msg.ToString());
                    count++;
                }

                await Task.Delay(40);
                if (count % 10 == 0)
                    Console.WriteLine("Messages sent: " + count);
            }
        }

        public static MsWebSocketConnection Connect()
        {
            var remote = new Uri("ws://51.116.113.211");
            var connection = new MsWebSocketConnection(remote);
            connection.SubscribeToEvents(HandleStateChange);

            Task.Run(ConnectAndStartListening).ConfigureAwait(false);

            async Task ConnectAndStartListening()
            {
                try
                {
                    Debug("Connecting socket");
                    await connection.StartConnectionAsync();
                    Debug("Socket connected");

                    await connection.Receive(HandleMessageReceived);
                }
                catch (Exception ex)
                {
                    Error("Socket couldn't connect. Error: " + ex.Message);
                }
            }

            return connection;
        }

        private static void HandleMessageReceived(string message)
        {
            Info("⬇ " + message);
        }


        public static void HandleStateChange(MsWebSocketConnection.ConnectionState state, Exception error)
        {
            Info($"Transport State: {state}. Error is {error?.Message ?? "empty"}. {error?.StackTrace}");
        }
    }
}