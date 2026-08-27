---
id: QS-DN-005
title: Accept and propagate CancellationToken through async call chains
technology: .NET
category: async-hygiene
kinds: [code]
severity: high
autofixable: false
tier: core
status: active
since: 2026-08-27
---

## Statement

A public `async Task`/`async Task<T>` method that does I/O takes a
`CancellationToken cancellationToken` parameter and passes it to every async
call it makes, rather than swallowing it with `CancellationToken.None` or
`default` partway down the chain.

## Rationale

A token that stops propagating partway down a call chain silently defeats
cancellation for everything below that point: a request abort, a timeout, or
a shutdown signal stops cutting work short exactly where the token was
dropped, and the caller has no way to see that from the method signature. The
public async methods in `RepositoryRegistry` (`CreateAsync`, `UpdateAsync`,
`ArchiveAsync`) all thread the token through to their own async work
(`PersistAsync(cancellationToken)`); that's the shape every new async method
in this codebase should match.

## Good example

```csharp
// RepositoryRegistry.cs:91
public async Task<RepositoryRegistration> CreateAsync(
    RepositoryRegistrationRequest request, CancellationToken cancellationToken)
{
    // ...
    await PersistAsync(cancellationToken).ConfigureAwait(false);
    return registration;
}
```

## Bad example

```csharp
public async Task<RepositoryRegistration> CreateAsync(
    RepositoryRegistrationRequest request, CancellationToken cancellationToken)
{
    // ...
    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
    // the caller's cancellation/timeout can no longer stop the persist step
}
```
