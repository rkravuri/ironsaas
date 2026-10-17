# Iron.Host.Api

The API host of one tenant unit. Bound to exactly one tenant at start (TF-003). Serves the tenant's hostnames under `/api`, `/auth`, `/connect`, `/.well-known` and `/health`. Also carries the `migrate` and `seed` commands that the fleet runs against a new tenant database (T7).

A host composes modules; it holds no business logic of its own. Modules are listed explicitly in `Program.cs` (no assembly scanning), so a reader can see what runs in this process.
