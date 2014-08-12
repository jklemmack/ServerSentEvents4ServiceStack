using ServiceStack.Serialization;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SSServerSentEvents
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

    public class JsonEventSourceClient
    {
        internal bool isReconnecting = false;
        internal string lastEventId = null;

        public string Uri { get; set; }
        public int RetryMS { get; private set; }
        Dictionary<string, Tuple<object, Type, MethodInfo>> actions = new Dictionary<string, Tuple<object, Type, MethodInfo>>();
        Action<ServerSentEvent> defaultListener;

        ConnectionState currentState;
        private CancellationToken stopToken;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();


        public enum EventSourceState
        {
            CONNECTING,
            OPEN,
            CLOSED
        }

        private ConnectionState CurrentState
        {
            get { return currentState; }
            set
            {
                if (!value.Equals(currentState))
                {
                    currentState = value;
                    //OnStateChanged(mCurrentState.State);
                }
            }
        }


        public JsonEventSourceClient(string uri, int retryMS = 3000)
        {
            this.Uri = uri;
            this.RetryMS = retryMS;

            CurrentState = new ClosedState(this);
        }

        internal void DispatchEvent(ServerSentEvent @event)
        {
            Action<ServerSentEvent> action = DispatchEventAsync;
            new Task(() =>
            {
                DispatchEventAsync(@event);
            }, this.stopToken).Start();
        }

        private void DispatchEventAsync(ServerSentEvent @event)
        {
            Tuple<object, Type, MethodInfo> target = null;

            // See if we have a listener for our event type
            if (string.IsNullOrEmpty(@event.EventType) || !actions.TryGetValue(@event.EventType, out target))
            {
                if (defaultListener != null)
                    defaultListener(@event);

                return;
            }

            // Get the type
            Type T = target.Item2;
            MethodInfo method = target.Item3;

            try
            {
                MethodInfo createObject = typeof(JsonSerializer)
                    .GetMethod("DeserializeFromString", new Type[] { typeof(System.String) });


                MethodInfo createGenericObject = createObject.MakeGenericMethod(T);
                object obj = createGenericObject.Invoke(null, new object[] { @event.Data });

                method.Invoke(target.Item1, new object[] { @event, obj });
            }
            catch (Exception ex)
            {
                // Couldn't cast or dispatch our event to the listener.  Go to the default listener
                if (defaultListener != null)
                    defaultListener(@event);
            }
        }

        public void ListenAll(Action<ServerSentEvent> action)
        {
            defaultListener = action;
        }

        public void ListenFor<T>(string @event, Action<ServerSentEvent, T> action)
        {
            if (actions.ContainsKey(@event))
                throw new ArgumentException(string.Format("Already listening for event \"{0}\"", @event), "event");

            MethodInfo method = action.Method;
            object target = action.Target;
            actions.Add(@event, new Tuple<object, Type, MethodInfo>(target, typeof(T), method));
        }

        public void StopListening(string @event)
        {
            if (actions.ContainsKey(@event))
                actions.Remove(@event);
        }

        public void Start(CancellationToken stopToken)
        {
            if (CurrentState.State == EventSourceState.CLOSED)
            {
                this.isReconnecting = false;
                this.stopToken = stopToken;
                tokenSource = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                Run();
            }
        }

        public void Start()
        {
            CancellationToken token = new CancellationToken();
            this.Start(token);
        }

        public void Stop()
        {
            if (CurrentState.State != EventSourceState.CLOSED)
            {
                tokenSource.Cancel();
            }
        }

        private void Run()
        {
            if (tokenSource.IsCancellationRequested && CurrentState.State == EventSourceState.CLOSED)
                return;

            currentState.Run(this.DispatchEvent, tokenSource.Token).ContinueWith(cs =>
            {
                CurrentState = cs.Result;
                Run();
            });
        }

        internal abstract class ConnectionState
        {
            internal JsonEventSourceClient Client;

            public ConnectionState(JsonEventSourceClient client)
            {
                this.Client = client;
            }

            public abstract EventSourceState State { get; }

            public abstract Task<ConnectionState> Run(Action<ServerSentEvent> MsgReceivedCallback, CancellationToken cancelToken);
        }

        internal class ClosedState : ConnectionState
        {
            public ClosedState(JsonEventSourceClient client) : base(client) { }

            public override EventSourceState State { get { return EventSourceState.CLOSED; } }

            public override Task<ConnectionState> Run(Action<ServerSentEvent> donothing, CancellationToken cancelToken)
            {
                if (cancelToken.IsCancellationRequested)
                    return Task.Factory.StartNew<ConnectionState>(() => { return new ClosedState(this.Client); });
                else
                {
                    // Attempt to automatically reconnect
                    Task baseTask = Task.Run(() => { });
                    if (this.Client.isReconnecting)
                    {
                        baseTask = Task.Run(() => Thread.Sleep(3000), cancelToken);
                    }
                    return baseTask.ContinueWith<ConnectionState>((t) => { return new ConnectingState(this.Client); });

                }
            }
        }

        internal class ConnectingState : ConnectionState
        {

            public ConnectingState(JsonEventSourceClient client) : base(client) { }

            public override EventSourceState State { get { return EventSourceState.CONNECTING; } }

            public override Task<ConnectionState> Run(Action<ServerSentEvent> MsgReceivedCallback, CancellationToken cancelToken)
            {
                // Try connecting
                var wreq = (HttpWebRequest)WebRequest.Create(this.Client.Uri);
                wreq.Method = "GET";

                if (!string.IsNullOrEmpty(this.Client.lastEventId))
                    wreq.Headers.Add("LastEventId", this.Client.lastEventId);

                var taskResp = Task.Factory.FromAsync<WebResponse>(wreq.BeginGetResponse, wreq.EndGetResponse, null)
                    .ContinueWith<HttpWebResponse>(t => (HttpWebResponse)t.Result);

                return taskResp.ContinueWith<ConnectionState>(task =>
                {
                    HttpWebResponse response = task.Result;
                    if (task.Status == TaskStatus.RanToCompletion && !cancelToken.IsCancellationRequested)
                    {
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            return new OpenState(this.Client, response);
                        }

                        // If 404 (or other as specified), do NOT try to reconnect - force stop token?
                    }
                    this.Client.isReconnecting = true;
                    return new ClosedState(this.Client);
                });

            }
        }

        internal class OpenState : ConnectionState
        {
            HttpWebResponse response;
            string remainingText = string.Empty;
            string[] lineEndings = new string[] { "\n", "\r", "\r\n" };
            Regex reEvent = new Regex("(\n|\r|\r\n){2}", RegexOptions.Compiled | RegexOptions.Multiline);

            private ServerSentEvent serverEvent = null;

            public OpenState(JsonEventSourceClient client, HttpWebResponse response)
                : base(client)
            {
                this.response = response;
            }

            public override EventSourceState State { get { return EventSourceState.OPEN; } }

            public override Task<ConnectionState> Run(Action<ServerSentEvent> MsgReceivedCallback, CancellationToken cancelToken)
            {
                Task<ConnectionState> task = new Task<ConnectionState>(() =>
                {
                    var stream = response.GetResponseStream();
                    byte[] buffer = new byte[1024 * 8];
                    var taskRead = stream.ReadAsync(buffer, 0, buffer.Length, cancelToken);

                    try
                    {
                        taskRead.Wait(cancelToken);
                    }
                    catch (Exception ex)
                    {
                        // how to handle exceptions?
                    }
                    if (cancelToken.IsCancellationRequested || !taskRead.IsCompleted)
                    {
                        this.Client.isReconnecting = true;
                        return new ClosedState(this.Client);
                    }
                    int bytesRead = taskRead.Result;
                    string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    text = remainingText + text;

                    string[] lines = SplitIntoLines(text, out remainingText);
                    //string[] lines = reEvent.Split(text);

                    foreach (string line in lines)
                    {
                        if (cancelToken.IsCancellationRequested) break;

                        // blank line => dispatch event
                        if (string.IsNullOrEmpty(line.Trim()) && serverEvent != null)
                        {
                            this.Client.DispatchEvent(serverEvent);
                            serverEvent = null;
                        }

                        // Begins with ":" => ignore (comment)
                        else if (line.StartsWith(":")) { }

                        // Anything else
                        else
                        {
                            string fieldName = String.Empty;
                            string fieldValue = String.Empty;
                            if (line.Contains(':'))
                            {
                                int index = line.IndexOf(':');
                                fieldName = line.Substring(0, index);
                                fieldValue = line.Substring(index + 1).TrimStart();
                            }
                            else
                                fieldName = line;

                            serverEvent = serverEvent ?? new ServerSentEvent();
                            switch (fieldName.ToLower())
                            {
                                case "event":
                                    serverEvent.EventType = fieldValue;
                                    break;
                                case "data":
                                    serverEvent.Data = fieldValue + "\n";
                                    break;
                                case "id":
                                    serverEvent.LastEventId = fieldValue;
                                    break;
                                case "retry":
                                    int retry = 0;
                                    if (Int32.TryParse(fieldValue, out retry))
                                        serverEvent.Retry = retry;
                                    break;
                                default:
                                    // Invalid 
                                    break;
                            }
                        }

                    }

                    // If no cancel requested, keep going
                    if (!cancelToken.IsCancellationRequested)
                        return this;

                    // Else we disconnect
                    this.Client.isReconnecting = true;
                    return new ClosedState(this.Client);
                }
                );

                task.Start();
                return task;
            }

            private static string[] SplitIntoLines(string text, out string remainingText)
            {
                List<string> lines = new List<string>();

                int lineLength = 0;
                char previous = char.MinValue;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\n' || c == '\r')
                    {
                        bool isCRLFPair = previous == '\r' && c == '\n';

                        if (!isCRLFPair)
                        {
                            string line = text.Substring(i - lineLength, lineLength);
                            lines.Add(line);
                        }

                        lineLength = 0;
                    }
                    else
                    {
                        lineLength++;
                    }
                    previous = c;
                }

                // Save the last chars that is not followed by a lineending.
                remainingText = text.Substring(text.Length - lineLength);

                return lines.ToArray();
            }
        }

    }
}
