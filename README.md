Server-Sent Events for ServiceStack
=============================

A simple, low-protocol-overhead implementation of 
[Server-Sent Events](dev.w3.org/html5/eventsource/)
 implemented as a [ServiceStack](http://www.servicestack.net) plugin.  

The bundled ServiceStack [Server-Sent Events imlementation](https://github.com/ServiceStackApps/Chat#server-sent-events) adds
additional data details and targets the rapid development of a certain high-level use cases, like the demo Chat app.  

This library is inteded to be more bare-bones, allowing an overhead-free implementation, with various options
to help build more complicated implementations, such as groups.

Usage
=======
Build and reference the DLL in your ServiceStack project.

Add the plugin in your `AppHost.Configure` method:

     Plugins.Add(new SimpleServerEventsFeature()
     {
         OnConnected = (client, metaData) => { },
         OnCreated = (client, req) => { },
         StreamPath = "/sse",
         Timeout = new TimeSpan(0, 0, 30)
     });


The client library is also included, under `JsonEventSourceClient`.  
It leverages the `ServiceStack.Text` JSON libraries to provide
 strongly-typed, delegate callbacks when events are received.

    JsonEventSourceClient client = new JsonEventSourceClient("http://localhost:1337/sse");
    client.ListenFor<ExampleDTO>("example", (eventObj, dto) =>
    {
        // dto is an instance of the ExampleDTO class   
    });

The client library is inspired by erizet's [EventSource4Net](https://github.com/erizet/EventSource4Net) library.