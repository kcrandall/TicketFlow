# TicketFlow.Integrations.WalFlow.Sinks

Message broker sink (`walflow`) for the TicketFlow integration event stream through [WalFlow](https://github.com/kcrandall/WalFlow)
sink packages. References only `WalFlow.Abstractions`; add the broker package you need (`WalFlow.Sinks.Kafka`, `.RabbitMq`, `.SQS`,
`.SNS`, `.AzureServiceBus`, `.GooglePubSub`, `.Kinesis`, `.RedisStreams`) at the same version (`0.1.0-alpha.alpha.20260708090623`).

```csharp
services.AddTicketFlow().AddEvents().AddWalFlowEventSinks(options =>
    options.Add(new SinkRegistration("kafka", kafkaSink), new EventScopeRule("tenant-a")));   // allowed scopes are required
// subscription: new EventSinkRef("walflow", "kafka")
```

- Each event is a Debezium-compatible record: `op = "c"`, `key = {id, subject, partitionKey}` (conversation or contact id, for
  per-conversation partition affinity), `after` = the CloudEvent, `source.table` = the event area (`conversation`, `contact`, `sla`,
  `routing`, `survey`, `channel`). Events are sent in order and the sink is flushed once per delivery.
- Named sinks are allow-listed per scope (`EventScopeRule`: tenant plus optional project/environment/app/workspace); subscriptions of
  other scopes cannot target them.
- `CloudEventSinkPayloadTransformer` publishes the structured CloudEvent (`application/cloudevents+json`) as the message body for sinks
  that accept an `ISinkPayloadTransformer`.
- `RequireDurableSinks` refuses sinks that do not declare a provider durability boundary (`ISinkDurability`); otherwise delivery is
  only as durable as the chosen sink (for example the RabbitMQ sink declares `NoStableBoundary`).
