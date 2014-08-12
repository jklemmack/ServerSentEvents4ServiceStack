using ServiceStack;
using ServiceStack.Auth;
using ServiceStack.Host.Handlers;
using ServiceStack.Web;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace SSServerSentEvents
{
    public class SimpleServerEventsFeature : IPlugin
    {
        public string StreamPath { get; set; }
        //public string HeartbeatPath { get; set; }

        public TimeSpan Timeout { get; set; }
        //public TimeSpan HeartbeatInterval { get; set; }


        public Action<EventClient, IRequest> OnCreated { get; set; }
        public Action<EventClient, Dictionary<string, string>> OnConnected { get; set; }

        //public Action<IEventSubscription, IRequest> OnCreated { get; set; }
        // Connected
        // Disconnected

        public SimpleServerEventsFeature()
        {
            StreamPath = "/sse";
            //HeartbeatPath = "/sse-heartbeat";

            Timeout = TimeSpan.FromSeconds(30);
            //HeartbeatInterval = TimeSpan.FromSeconds(10);

        }

        public void Register(IAppHost appHost)
        {
            var broker = new MemoryServerEventsBroker()
            {
                //Timeout = Timeout,
            };



            var container = appHost.GetContainer();

            if (container.TryResolve<IServerEventsBroker>() == null)
                container.Register<IServerEventsBroker>(broker);

            appHost.RawHttpHandlers.Add(httpReq =>
                httpReq.PathInfo.EndsWith(StreamPath)
                    ? (IHttpHandler)new ServerEventsHandler()
                    //: httpReq.PathInfo.EndsWith(HeartbeatPath)
                    //  ? new ServerEventsHeartbeatHandler()
                      : null);


        }


        public class ServerEventsHandler : HttpAsyncTaskHandler
        {
            public override bool RunAsAsync()
            {
                return true;
            }

            public override Task ProcessRequestAsync(IRequest req, IResponse res, string operationName)
            {
                res.ContentType = MimeTypes.ServerSentEvents;
                res.AddHeader(HttpHeaders.CacheControl, "no-cache");
                res.KeepAlive = true;
                res.Flush();

                IAuthSession session = req.GetSession();
                var userAuthId = session != null ? session.UserAuthId : null;
                var feature = HostContext.GetPlugin<SimpleServerEventsFeature>();

                //var now = DateTime.UtcNow;
                var subscriptionId = SessionExtensions.CreateRandomSessionId();
                var eventClient = new EventClient(res)
                {
                    SubscriptionId = subscriptionId,     // A session can have multiple subscriptions / clients
                };

                if (feature.OnCreated != null)
                    feature.OnCreated(eventClient, req);

                //var heartbeatUrl = req.ResolveAbsoluteUrl("~/".CombineWith(feature.HeartbeatPath))
                //    .AddQueryParam("id", subscriptionId);

                var privateArgs = new Dictionary<string, string>(eventClient.Meta) {
                        {"id", subscriptionId },
                        //{"heartbeatUrl", heartbeatUrl},
                        //{"heartbeatIntervalMs", ((long)feature.HeartbeatInterval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) }
                };

                // Register the client
                req.TryResolve<IServerEventsBroker>().Connect(eventClient);

                if (feature.OnConnected != null)
                    feature.OnConnected(eventClient, privateArgs);

                // Welcome our new client
                res.OutputStream.Write(": " + subscriptionId + " connected\n\n");     // Write initial stream so the client knows we're alive
                res.Flush();
                var tcs = new TaskCompletionSource<bool>();

                eventClient.OnDispose = client =>
                {
                    try
                    {
                        res.EndHttpHandlerRequest(skipHeaders: true);
                    }
                    catch { }
                    tcs.SetResult(true);
                };

                return tcs.Task;
            }
        }

        public class ServerEventsHeartbeatHandler : HttpAsyncTaskHandler
        {
            public override bool RunAsAsync() { return true; }

            public override Task ProcessRequestAsync(IRequest req, IResponse res, string operationName)
            {
                //req.TryResolve<IMyServerEvents>().Pulse(req.QueryString["id"]);
                res.EndHttpHandlerRequest(skipHeaders: true);
                return ((object)null).AsTaskResult();
            }
        }

        public interface IServerEventsBroker
        {
            void NotifyAll(string eventName, object message);
            void NotifyClient(string clientId, string eventName, object message);
            void NotifyGroup(string groupName, string eventName, object message);
            //void Pulse(string id);
            void Connect(EventClient subscription);      // Connects a new client
            void Disconnect(EventClient subscription);    // Disconnects the client

        }

        public class MemoryServerEventsBroker : IServerEventsBroker
        {
            ConcurrentDictionary<string, EventClient> clients = new ConcurrentDictionary<string, EventClient>();

            public void Connect(EventClient client)
            {
                client.OnUnsubscribe = HandleUnsubscribe;
                clients.AddOrUpdate(client.SubscriptionId, client, (id, newClient) => newClient);
                Debug.WriteLine("New client connected: {0}", client.SubscriptionId);
            }

            public void Disconnect(EventClient client)
            {
                EventClient dummy;
                clients.TryRemove(client.SubscriptionId, out dummy);
            }


            public void NotifyClient(string clientId, string eventName, object message)
            {
                throw new NotImplementedException();
            }

            public void NotifyGroup(string groupName, string eventName, object message)
            {
                throw new NotImplementedException();
            }

            public void NotifyAll(string eventName, object message)
            {
                foreach (var client in clients)
                {
                    client.Value.Publish(eventName, message);
                }


            }

            private void HandleUnsubscribe(EventClient client)
            {
                client.Dispose();
            }

            //public void Pulse(string id)
            //{

            //}
        }

        public class EventClient : IMeta, IDisposable
        {
            private readonly IResponse response;
            private long msgId;
            public string SubscriptionId { get; set; }

            public EventClient(IResponse response)
            {
                this.msgId = 0;
                this.response = response;
                this.Meta = new Dictionary<string, string>();
            }

            public Dictionary<string, string> Meta { get; set; }

            public Action<EventClient> OnUnsubscribe { get; set; }
            public Action<EventClient> OnDispose { get; set; }

            public void Publish(string @event, object message)
            {
                try
                {
                    var data = (message != null ? message.ToJson() : "");

                    //StringBuilder sb = new StringBuilder(":\n");    //Empty comment line @ top
                    
                    StringBuilder sb = new StringBuilder("id: " + Interlocked.Increment(ref msgId) + "\n");
                    if (!@event.IsNullOrEmpty()) sb.Append("event: " + @event + "\n");

                    // Only send id when there is some message
                    if (!data.IsNullOrEmpty()) sb.Append("id: " + Interlocked.Increment(ref msgId) + "\ndata: " + data + "\n");

                    sb.Append("\n");    //2nd new line to dispatch the data
                    //var frame = "";
                    //frame += "id: " + Interlocked.Increment(ref msgId) + "\n";
                    //frame += "event: " + @event + "\n";
                    //frame += "data: " + data + "\n";
                    //frame += "\n";

                    lock (response) // Do this to serialize the output, just in case
                    {
                        response.OutputStream.Write(sb.ToString());
                        response.Flush();
                    }
                }
                catch (Exception)
                {
                    // Failed somehow... log?
                    Unsubscribe();
                }
            }

            public void Unsubscribe()
            {
                if (OnUnsubscribe != null)
                    OnUnsubscribe(this);
            }

            public void Dispose()
            {
                OnUnsubscribe = null;
                try
                {
                    lock (response)
                    {
                        response.EndHttpHandlerRequest(skipHeaders: true);
                    }
                }
                catch (Exception ex)
                {
                    //Log.Error("Error ending subscription response", ex);
                }

                if (OnDispose != null)
                    OnDispose(this);
            }
        }
    }
}
