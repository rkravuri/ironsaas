# Iron.Host.Fleet

The fleet orchestrator: the worker of the control plane. Runs provisioning jobs and later release waves, migrations and backups. On the laptop it creates tenant units as Compose projects; on Azure as Kubernetes namespaces. It is the only process with rights to create tenant objects (T14).

A host composes modules; it holds no business logic of its own. Modules are listed explicitly in `Program.cs` (no assembly scanning), so a reader can see what runs in this process.
