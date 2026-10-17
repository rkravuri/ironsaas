# Iron.Host.ControlPlane

The control plane API: tenant registry, provisioning requests, later entitlements, flags definitions, billing and the operator console API. Shared by all tenants of a region; it holds no business data (T13).

A host composes modules; it holds no business logic of its own. Modules are listed explicitly in `Program.cs` (no assembly scanning), so a reader can see what runs in this process.
