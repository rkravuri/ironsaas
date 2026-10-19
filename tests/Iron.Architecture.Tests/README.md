# Iron.Architecture.Tests

The build checks of the repository, written as plain xUnit tests so that anyone can read them.

| Rule | Where | What it checks |
|---|---|---|
| Layers | `LayerRules`, `LayerBuildCheckTests` | Every project declares `<IronLayer>` in its own file: `Kernel`, `Business`, `Products`, `Host`, `Test` or `Tool` (ADR-0029). The layer agrees with the name and the folder: `Iron.Kernel*` under `src/Kernel/`, `Iron.Business.*` under `src/Business/`, `Iron.Products.*` under `src/Products/`, `Iron.Host.*` under `src/Hosts/`, tests under `tests/`, tools under `tools/`. The build's own target `IronCheckLayer` (in `Directory.Build.props`) stops a project without a valid layer with `IRON0001` or `IRON0002`; `LayerBuildCheckTests` proves it on fixture projects. |
| Project references | `ModuleBoundaryRules` | Dependencies go downward only: Products may use Business and Kernel, Business may use Kernel, Kernel uses only Kernel. The vocabulary `Iron.Kernel` references nothing. A kernel library references the vocabulary and kernel libraries only. A Contracts project references `Iron.Kernel` only. A module references the vocabulary, the kernel libraries and Contracts projects only, never another module. A host references projects of the three layers, never another host, a test or a tool. |
| Private namespaces | `AssemblyReferenceRules` | No compiled assembly refers to a type in another module's `Api`, `Services` or `Data` namespace, whatever the layer. The check reads the type references of each assembly, so it also sees uses inside method bodies. |
| System clock | `ForbiddenMemberRules` | No Foundation assembly reads `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now` or `DateTimeOffset.UtcNow`. Time comes from an injected `TimeProvider` (ADR-0007). The check reads member references, so it sees every call site. |
| Model rules | `ModuleDbContextTests` | Every `ModuleDbContext` in a module assembly builds its model and passes `ModelCheck`: every entity derives from `Entity`; in the Business and Products layers every entity is a `BusinessRecord` or marked `ITenantWide` (TF-220). |
| Layout | `RepositoryLayoutTests` | Every project sits in a folder with its own name; a Contracts project sits in `Contracts/` inside its module; every module has a Contracts project; project names are unique; projects under `src/` start with `Iron.`. |

Each failure names the project file or the assembly and the forbidden reference.

How the rules are organised: `ProjectCatalog` finds every project file in the repository (build output and packages left out) and describes it by its declared layer and its name (pure logic, unit tested with made-up paths in `Fixture`). `LayerRules`, `ModuleBoundaryRules`, `AssemblyReferenceRules` and `ForbiddenMemberRules` are pure functions over that data. The `*Tests` classes apply them to the real repository.

Adding a rule: write it as a pure function with its own unit tests, then add one test that applies it to the repository.

Try it: add `<ProjectReference Include="../Iron.Kernel.Identity/Iron.Kernel.Identity.csproj" />` to `src/Kernel/Iron.Kernel.Audit/Iron.Kernel.Audit.csproj`, or delete the `<IronLayer>` line of any project, and run `make check`. Undo it afterwards.
