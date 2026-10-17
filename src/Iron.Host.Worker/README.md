# Iron.Host.Worker

The worker host of one tenant unit. Runs jobs, the outbox and later notifications and webhooks for its tenant only. In S1 it starts, binds to its tenant and emits a heartbeat trace that carries the tenant id (T7).

A host composes modules; it holds no business logic of its own. Modules are listed explicitly in `Program.cs` (no assembly scanning), so a reader can see what runs in this process.
