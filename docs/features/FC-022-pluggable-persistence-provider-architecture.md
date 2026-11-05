# FC-022 — Pluggable Persistence Provider Architecture

## Goal

FC-022 separates the durable observation-ingestion contract from the concrete persistence technology used to implement it.

The central invariant is:

```text
available providers != active provider
```

A provider package may make a persistence implementation available without registering `IObservationIngestionStore` directly. The neutral persistence layer finalizes selection after providers are registered and owns the single active store registration.

## Architecture

```text
composition root
    |
    +-- register available provider factories
    |       |
    |       +-- InMemory
    |       +-- future SqlServer
    |       +-- future PostgreSql
    |       +-- future MongoDb
    |
    +-- AddFactoryConnectPersistence(configuration)
            |
            v
   validate available providers
            |
            v
   validate unique provider keys
            |
            v
   select Persistence:Provider
            |
            v
   install one store descriptor
            |
            v
   activate selected provider lazily
            |
            v
 IObservationIngestionStore
```

`FactoryConnect.Persistence` is provider-neutral. It does not contain a switch statement and does not know concrete provider names.

## Selection Contracts

`PersistenceOptions` owns the configured provider key after normalization.

`PersistenceProviderKey` normalizes keys by trimming whitespace and applying invariant case normalization. Provider matching and duplicate detection therefore use the same canonical representation.

`IPersistenceProviderRegistration` describes how an available provider can create an ingestion store:

```csharp
public interface IPersistenceProviderRegistration
{
    string ProviderKey { get; }

    IObservationIngestionStore Create(IServiceProvider services);
}
```

Registration does not activate the provider.

`PersistenceProviderRegistration` is the default factory-backed implementation of that contract.

## Composition Lifecycle

The supported lifecycle is:

```text
register provider factories
        |
finalize neutral persistence selection
        |
validate available providers
        |
validate unique normalized keys
        |
select configured key
        |
register one IObservationIngestionStore
        |
resolve store
        |
activate exactly one provider
```

Example:

```csharp
builder.Services.AddInMemoryPersistenceProvider();
builder.Services.AddFactoryConnectPersistence(
    builder.Configuration);
```

with configuration:

```json
{
  "Persistence": {
    "Provider": "InMemory"
  }
}
```

`AddFactoryConnectPersistence` is the persistence finalization boundary. Provider registrations must already be present before it is called. Direct `IObservationIngestionStore` registrations are rejected before the selector installs the final store descriptor.

The in-memory provider key belongs to `FactoryConnect.Infrastructure`, not to `FactoryConnect.Persistence`.

## Composition-Root Invariant

`AddFactoryConnectPersistence` must be the final persistence-related composition operation.

`IServiceCollection` remains mutable after this call. Microsoft DI does not provide a mechanism for an extension method to seal the collection or prevent arbitrary later registrations.

A direct `IObservationIngestionStore` registration made after persistence finalization is therefore outside the supported composition lifecycle and may override the selected provider.

FC-022 enforces every boundary it owns:

- direct store registrations present during finalization are rejected;
- provider registrations through `AddPersistenceProvider` after finalization are rejected;
- the composition root is responsible for performing no persistence-related mutations after finalization.

## Failure Semantics

FC-022 fails explicitly when:

- `Persistence:Provider` is missing or whitespace;
- no provider has been registered before persistence finalization;
- the configured provider key has no registration;
- two available providers normalize to the same key;
- another component has already registered `IObservationIngestionStore` directly;
- provider registration is attempted through the persistence registration API after finalization;
- an activated provider factory returns no store.

These failures prevent ambiguous persistence ownership within the supported composition lifecycle.

## Exactly-One Activation

Only the selected registration's factory is invoked.

Registering multiple available providers does not instantiate them. Finalization validates and selects a registration but still does not create the concrete store. The selected provider is activated only when `IObservationIngestionStore` is resolved.

The active `IObservationIngestionStore` is registered as a singleton, so repeated resolution returns the same activated store.

## Current Provider

FC-022 keeps the existing `InMemoryObservationIngestionStore` as the initial provider implementation.

`AddInMemoryPersistenceProvider()` registers only its provider factory. It does not directly register `IObservationIngestionStore`.

This preserves existing FC-020/FC-021 ingestion and checkpoint behavior while moving persistence choice to composition time.

## Store Conformance Suite

FC-022 extracts the FC-020 durable-ingestion behavior into a reusable provider conformance suite:

```csharp
public abstract class ObservationIngestionStoreConformanceTests
{
    protected abstract IObservationIngestionStore CreateStore();

    protected abstract int ReadObservationCount(
        IObservationIngestionStore store,
        ObservationStreamId streamId);
}
```

The behavioral scenarios remain shared, while provider-specific subclasses supply store creation and a test-only observation-count inspection hook.

`InMemoryObservationIngestionStoreConformanceTests` runs the complete FC-020 behavior against the current in-memory provider. FC-023 can derive a SQL Server implementation from the same suite and implement the observation inspection hook through provider-specific test infrastructure.

The shared behavior covers:

- atomic observation and checkpoint commit;
- expected-checkpoint concurrency guards;
- same-instance checkpoint advancement;
- idempotent replay;
- prevention of replay augmentation;
- checkpoint-regression rejection;
- invalid-observation atomicity;
- empty-batch checkpoint advancement;
- conflicting duplicate rejection;
- stream isolation;
- cancellation semantics;
- sequence-bound validation;
- stale expected-checkpoint rejection;
- explicit MTConnect instance transition.

## Extension Model

Future providers follow the same pattern without modifying the neutral persistence assembly:

```text
FactoryConnect.Persistence.SqlServer
    |
    +-- AddSqlServerPersistenceProvider(...)
            |
            +-- provider key
            +-- provider configuration
            +-- factory
            +-- concrete IObservationIngestionStore
```

A future provider package can therefore be added through normal composition-root registration rather than a central provider switch.

## Scope

Included in FC-022:

- neutral `FactoryConnect.Persistence` project;
- persistence project included as a first-class solution project;
- persistence provider options and key normalization;
- provider registration/factory contract;
- provider-before-finalization composition order;
- available-provider validation during finalization;
- duplicate-key validation;
- configured-provider selection;
- exactly-one lazy store activation;
- rejection of direct competing store registrations present at finalization;
- in-memory provider registration through the new model;
- Edge composition through `Persistence:Provider`;
- analyzer-safe test naming;
- reusable FC-020 store conformance suite;
- tests proving provider-selection invariants.

Not included:

- SQL Server implementation;
- MongoDB implementation;
- PostgreSQL implementation;
- migrations or schema management;
- provider-specific connection-string validation;
- runtime provider switching;
- multiple active ingestion stores.

Those belong to later provider-specific slices such as FC-023.
