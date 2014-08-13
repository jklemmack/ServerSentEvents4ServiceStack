using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleServerEvents
{
    public class ServerSentEvent
    {
        public string LastEventId { get; set; }
        public string EventType { get; set; }
        public string Data { get; set; }
        public int? Retry { get; set; }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("EventType: ").Append(EventType).AppendLine();
            sb.Append("Data: ").Append(Data).AppendLine();
            sb.Append("LastEventId: ").Append(LastEventId).AppendLine();
            if (Retry.HasValue)
                sb.Append("Retry: ").Append(Retry.Value).AppendLine();
            return sb.ToString();
        }
    }

    public class JsonEventSourceClient2
    {
        public enum EventSourceState
        {
            CONNECTING = 0,
            OPEN,
            CLOSED
        }


        private enum State
        {
            Disconnected,
            Connected,
            Reconnecting
        }

        public string Uri { get; set; }
        public int RetryMS { get; private set; }

        Dictionary<string, Tuple<object, Type, MethodInfo>> actions = new Dictionary<string, Tuple<object, Type, MethodInfo>>();
        Action<ServerSentEvent> defaultListener;
        private CancellationToken stopToken;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();

        private State currentState = State.Disconnected;
        public JsonEventSourceClient2(string uri)
        {
            stopToken = tokenSource.Token;

        }

        public void Connect()
        {
            Task.Factory.StartNew(() => StreamProcessorThread(), stopToken);
            Task.Factory.StartNew(() => NetworkThread(stopToken));
        }

        public void Disconnect()
        {

        }

        void NetworkThread(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                //if()
            }
        }

        void StreamProcessorThread()
        {

        }

    }



}
