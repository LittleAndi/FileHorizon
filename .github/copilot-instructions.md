# Copilot Instructions for FileHorizon (.NET 8)

These instructions define the desired architecture, layout, conventions, and guard‑rails for AI assisted code generation in this repository. All generated code should conform unless the user explicitly overrides a rule.

---

## 1. Solution Overview

We are building a modular, layered .NET 8 application. For now there are only TWO top-level runtime projects:

1. Host project (startup / composition root)
2. Application project (contains all functional layers: Core, Common, Infrastructure, Models, etc.)

Tests live under `test/` mirroring structure in `src/`.

Later we may extract sub-layers into separate projects (e.g., `FileHorizon.Domain`, `FileHorizon.Infrastructure`, etc.), so keep internal boundaries clean to ease future extraction.

---

## 2. Directory & Project Layout

Expected structure (initial):

```
src/
	FileHorizon.Host/                  # Entry point (ASP.NET Core or console); only startup, DI, config, logging, middleware
	FileHorizon.Application/           # All functional code (temporary aggregation)
		Common/                          # Cross-cutting helpers (logging abstractions, constants, result types)
		Core/                            # Core business rules, domain services, pure domain logic (no framework dependencies)
		Models/                          # DTOs, records, value objects (prefer immutable, avoid anemic classes)
		Infrastructure/                  # External integrations (data access, file storage, messaging, etc.)
		Abstractions/                    # Interfaces for external dependencies (implemented by Infrastructure)
		Features/                        # Vertical slices (feature folders): Command + Query + Handler (+ Validator/Mapping)
		Configuration/                   # Strongly typed options classes and config binding
		Mappings/                        # Manual mapping helpers (extension methods / static classes), isolated per concern
		Validation/                      # FluentValidation validators (if used), one per request/command
test/
	FileHorizon.Application.Tests/     # Unit tests for Core, Models, Services (no external dependencies)
	FileHorizon.Integration.Tests/     # Higher-level integration tests (optional early placeholder)

Solution file root (later):

```

FileHorizon.sln

```

---

## 3. Project Responsibilities

### FileHorizon.Host

Purpose: Pure composition. Minimal Program.cs + dependency injection, configuration binding, logging setup, middleware pipeline. No business logic.

### FileHorizon.Application

Temporary container for all core layers. Internal organization must enforce separation:

- `Core` (domain logic, pure, no framework dependencies where possible)
- `Models` (domain/value objects, DTO boundaries – avoid contamination with EF or framework attributes unless strictly necessary)
- `Common` (result pattern, errors, constants, primitive guards)
- `Infrastructure` (implementation details: persistence, external APIs). Keep behind interfaces declared in `Abstractions`.
- `Features` (optional vertical slice pattern: each feature folder may have Request/Response, Handler, Validator, Mapping)

---

## 4. Dependency Rules

1. Host -> Application (only). Host must not reference internal sub-namespaces directly except via public Application surface.
2. Inside `FileHorizon.Application`:
   - `Core` MUST NOT depend on `Infrastructure`.
   - `Infrastructure` MAY depend on `Core`, `Common`, `Abstractions`, `Models`.
   - `Common` should have zero outward dependencies (except BCL / well-known lightweight libs).
   - `Features` may depend on `Core`, `Models`, `Common`, `Abstractions`.
   - `Mappings`, `Validation` depend on what they map/validate but keep them thin.
3. No circular dependencies (enforce via design; consider adding dependency analyzer later).
4. All external service usage is behind interfaces in `Abstractions` so swapping implementations later is trivial.

When in doubt: High-level policies do not import low-level implementation details.

Also when adding package references, use dotnet add package.

---

## 5. Naming Conventions

- Namespace root: `FileHorizon.*`
- Classes: PascalCase; interfaces prefixed with `I` (e.g., `IFileRepository`).
- Async methods end with `Async`.
- Immutable record types for simple data carriers: `public sealed record FileMetadata(...);`
- Private fields: `_camelCase`.
- File names = type names (one public type per file, exceptions allowed for small related records).
- Avoid abbreviations unless industry-standard (e.g., `Id`, `DTO` in suffix only when helpful).

---

## 6. Coding Standards

- Target: .NET 8, enable nullable reference types and implicit usings.
- Prefer `record` / `record struct` for pure data models; `class` for behavior-rich or mutable aggregates.
- Use `sealed` unless a type is explicitly designed for inheritance.
- Favor constructor injection; avoid service locators / static singletons.
- Keep methods small and intention-revealing; apply SRP.
- Use guard clauses (custom guard helpers in `Common` if introduced) to validate inputs early.
- Return a `Result` (or similar pattern) instead of throwing for expected domain failures; exceptions reserved for truly exceptional conditions.
- CancellationTokens on all public async methods that perform I/O or long-running work.
- Logging: abstract behind `ILogger<T>`; no direct console writes outside Host.

---

## 7. Error & Result Handling

Introduce (or plan) a `Result<T>`/`Result` pattern in `Common`:

```

Result<T>.Success(value)
Result<T>.Failure(Error code, message)

```

Provide a central `Error` catalog (static class with nested domains or strongly typed discriminated style). Avoid scattering raw strings.

---

## 8. Configuration & Options

Use strongly typed options classes in `Configuration/` bound via `builder.Services.Configure<T>(...)`. Access via IOptionsSnapshot in business pathways where reload support is beneficial.

---

## 9. Persistence & Infrastructure (Future)

If EF Core is added later, isolate DbContext and configurations under `Infrastructure/Persistence/`. Do NOT sprinkle `[Table]` etc. attributes across domain models; use Fluent configurations. For now generate repository interfaces in `Abstractions/` and stub in-memory implementations inside `Infrastructure/`.

---

## 10. Mapping

We are NOT using AutoMapper / Mapster. Provide explicit, discoverable mappings:

- Use small static classes or extension methods inside `Mappings/` (e.g., `FileMetadataMappings`).
- Keep each method single-purpose: `ToDto`, `ToEntity`, etc.
- Avoid premature abstraction; duplicate a few lines over creating complex generic mapping utilities.
- All mapping methods must be deterministic and side‑effect free.
- Prefer returning new immutable records instead of mutating passed instances.

---

## 11. Validation

If FluentValidation is used, validators sit in `Validation/` or inside each `Feature/*` folder next to the request model. Keep one validator per request/command.

---

## 12. Testing Strategy

Test projects naming: `FileHorizon.*.Tests`.

Guidelines:

1. Unit tests for domain / core logic (no external dependencies) – fast and deterministic.
2. Use builder/test data object patterns to reduce duplication.
3. Avoid testing private methods – test behavior via public surface.
4. Consider snapshot tests only for stable DTO contracts (sparingly).
5. Ensure parallel-safe tests; disable parallelization only when necessary.

Suggested libraries: xUnit + FluentAssertions + NSubstitute (or Moq) + Bogus (for data generation) – adjust when actually added.

---

## 13. AI Generation Guard-Rails

When asking Copilot / AI to create code:

1. Always target .NET 8 features (e.g., primary constructors, file-scoped namespaces, required members where apt).
2. Enforce dependency rules (see section 4). Never let Infrastructure call upward into Core/Features.
3. For new features: create a folder under `Features/FeatureName/` with:
   - Request (Command/Query) record
   - Plain handler class named `<FeatureName>Handler` exposing a public `Task<Result<TResponse>> HandleAsync(<RequestType> request, CancellationToken ct)` method (no MediatR).
   - Validator (optional)
   - Mapping helper (optional) when transformation is non-trivial
4. Keep generated code minimal; no premature optimization.
5. Provide XML doc comments only for public APIs that are part of a stable contract; otherwise rely on clear naming.
6. Prefer returning `Result<T>` not throwing.
7. Include cancellation tokens in async signatures.
8. Do not add third-party dependencies without an explicit user request or a documented justification.

When uncertain, ASK for clarification before generating large swaths of code.

---

## 14. Style & Analyzers (Planned)

We will add an `.editorconfig` and possibly Roslyn analyzers (e.g., `dotnet_analyzers`, `stylecop`) later. Until then: follow these conventions manually.

---

## 15. Commit & PR Guidance

- Conventional commit style preferred (e.g., `feat: add file metadata value object`).
- Keep commits focused and logically grouped.
- Include high-level rationale when introducing new abstractions.

### 15.1 Semantic / Conventional Commit Format

We follow the Conventional (Semantic) Commit format to enable automated tooling (change logs, release notes) and keep history scannable.

Format:

```

<type>(<optional-scope>)<!?>: <subject>

[optional body]

[optional footer(s)]

```

Rules:

- `<subject>`: imperative, present tense ("add", not "added" or "adds"), no trailing period.
- Keep subject <= 72 chars when possible.
- Scope is optional; use lowercase hyphen/period separated tokens (e.g., `core`, `infrastructure`, `file-import`).
- Use `!` before the colon to signal a breaking change OR include a `BREAKING CHANGE:` footer.

Allowed `type` values:

- `feat` – user-facing feature.
- `fix` – bug fix.
- `docs` – documentation only changes.
- `style` – formatting / stylistic (white-space, commas, etc.) no code meaning change.
- `refactor` – code change that neither fixes a bug nor adds a feature.
- `test` – add or adjust tests only.
- `chore` – maintenance tasks (tooling, deps bumps, repo chores) with no production code impact.
- `build` – build system or external dependency changes (csproj, package updates that affect build).
- `ci` – CI/CD pipeline or workflow changes.
- `perf` – performance improvement.
- `revert` – revert a previous commit (subject should reference the hash).

Breaking changes:

- Indicate with `!` (e.g., `feat(core)!: rework result pipeline`) OR in body/footer:
  `BREAKING CHANGE: <explanation>`.

Examples:

```

feat(core): add Result<T> abstraction
fix(infrastructure): handle transient timeout in file storage client
docs(readme): clarify local dev setup steps
refactor(common): simplify guard clause helpers
style: apply dotnet format suggestions
test(core): add tests for FileMetadata validation
chore: bump dependencies and regenerate lock files
perf(core): optimize large file streaming buffer size
revert: revert feat(core): add Result<T> abstraction

```

Body guidance (optional):

- Wrap lines at ~100 columns.
- Explain motivation and contrast with previous behavior when not obvious.

Footer usage (optional):

- `BREAKING CHANGE: ...` for breaking changes (when `!` not used).
- References: `Refs #123`, `Closes #456`.

Pull Requests:

- Title should mirror a representative commit (prefer the primary change's conventional form).
- Squash merges should preserve/consolidate a clear conventional commit subject.
- If multiple types apply, split into multiple commits instead of combining.

Optional Git hook (future): Add a `commit-msg` hook to validate pattern (document separately if introduced).

---

## 16. Future Extraction Plan

As the codebase grows, we may split into multiple projects:

```

FileHorizon.Domain
FileHorizon.Application (CQRS / Orchestrations)
FileHorizon.Infrastructure
FileHorizon.Host

````

Design current internal folders so they can be lifted into these projects with minimal churn (avoid leaking infra types into core logic, keep namespaces consistent).

---

## 17. Security & Secrets

- No secrets in source control. Use user secrets or environment variables.
- If generating code that requires secrets (API keys, connection strings), produce placeholders and documentation only.

---

## 18. Performance Considerations

Premature optimization discouraged. However, enforce:

- Async all I/O
- Avoid blocking on `Task.Result` / `.Wait()`
- Use streaming (IAsyncEnumerable) where large enumerations are anticipated.

---

## 19. Example: Minimal Program.cs (Guidance)

When generated, prefer something like:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register application services
builder.Services.AddApplicationServices(); // Extension method to be created in Application project

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
````

And in `FileHorizon.Application` an extension:

```csharp
namespace FileHorizon.Application;

public static class ServiceCollectionExtensions
{
		public static IServiceCollection AddApplicationServices(this IServiceCollection services)
		{
				// Register Core / Infrastructure interfaces here (temporary until split)
				return services;
		}
}
```

---

## 20. Do / Don't Summary

DO:

- Keep Host thin
- Keep Core pure
- Abstract external dependencies
- Use clear, intention-revealing names
- Write tests for domain logic first

DON'T:

- Mix infrastructure concerns into Core
- Add libraries casually
- Leak EF or serialization attributes into domain models without reason
- Throw exceptions for expected domain outcomes

---

## 21. How to Request New Code from AI

When asking for new functionality, phrase requests like:
"Generate a new feature under Features/FileImport that validates and stores metadata for uploaded files, returning Result<FileMetadata>. Include handler, request, validator, and mapping stub."

This helps maintainers and AI stay aligned with conventions.

---

## 22. Amendments

Update this file whenever architectural decisions evolve. Keep it the single source of truth for code generation conventions.

---

End of Copilot Instructions.
