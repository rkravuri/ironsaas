# Iron.Architecture.Tests

The build checks of the repository, written as plain xUnit tests so that anyone can read them.

| Rule | Where | What it checks |
|---|---|---|
| Project references | `ModuleBoundaryRules` | A shared library references shared libraries only. A Contracts project references `Iron.Core` only. A module (or product) references shared libraries and Contracts projects only. A host references shared libraries, modules, products and Contracts projects only. |
| Private namespaces | `AssemblyReferenceRules` | No compiled assembly refers to a type in another module's `Api`, `Services` or `Data` namespace. The check reads the type references of each assembly, so it also sees uses inside method bodies. |
| Layout | `RepositoryLayoutTests` | Every project sits in a folder with its own name; a Contracts project sits in `Contracts/` inside its module; project names are unique; projects under `src/` start with `Iron.`. |

Each failure names the project file or the assembly and the forbidden reference.

How the rules are organised: `ProjectCatalog` finds and classifies every project file (pure logic, unit tested with made-up paths). `ModuleBoundaryRules` and `AssemblyReferenceRules` are pure functions over that data. The `*Tests` classes apply them to the real repository.

Adding a rule: write it as a pure function with its own unit tests, then add one test that applies it to the repository. Later slices add: standard columns and no cross-schema foreign keys (T4), a permission or anonymous marker on every endpoint (T8), owning organisation on every business record (T4), externalised UI strings (S4).

Try it: add `<ProjectReference Include="../Iron.Identity/Iron.Identity.csproj" />` to `src/Iron.Audit/Iron.Audit.csproj` and run `make check`. Remove it again afterwards.
