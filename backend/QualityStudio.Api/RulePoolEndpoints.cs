using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Mvc;

namespace QualityStudio.Api;

public sealed record RuleOverrideMutationRequest(bool? Enabled, string? Severity, string? Reason);

public sealed record CustomRuleMutationRequest(string? Content, string? Reason);

public sealed record CustomRuleValidationRequest(string? Content);

public sealed record RulePackMutationRequest(JsonElement? Pack, string? Reason);

public sealed record RuleApplicabilityMutationRequest(IReadOnlyList<string>? Packs, string? Reason);

public sealed record RuleSetImportRequest(JsonElement? RuleSet, string? Mode, bool DryRun, string? Reason);

/// <summary>
/// The rule pool management API: the resolved pool with its provenance, and the writes that change
/// it - overrides, custom rules, packs, applicability, rule-set import and export - each with an
/// audit entry. Every route exists unscoped (default repository) and under
/// <c>/api/repos/{repoId}</c>. The <c>scope</c> query parameter selects the repository's own
/// <c>.quality/rules/</c> (<c>project</c>, the default) or the host-wide data-root folder
/// (<c>global</c>), which only a client allowed to register repositories may change.
/// </summary>
internal static class RulePoolEndpoints
{
    public static void MapRulePoolEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/api", "/api/repos/{repoId}" })
        {
            app.MapGet(prefix + "/rules", Rules);
            app.MapGet(prefix + "/rules/export", Export);
            app.MapPost(prefix + "/rules/import", Import);
            app.MapGet(prefix + "/rules/audit", Audit);
            app.MapPut(prefix + "/rules/overrides/{ruleId}", SetOverride);
            app.MapDelete(prefix + "/rules/overrides/{ruleId}", RemoveOverride);
            app.MapPost(prefix + "/rules/custom/validate", ValidateCustomRule);
            app.MapPut(prefix + "/rules/custom/{ruleId}", PutCustomRule);
            app.MapDelete(prefix + "/rules/custom/{ruleId}", DeleteCustomRule);
            app.MapPut(prefix + "/rules/packs/{packId}", PutPack);
            app.MapDelete(prefix + "/rules/packs/{packId}", DeletePack);
            app.MapPut(prefix + "/rules/applicability", SetApplicability);
            app.MapDelete(prefix + "/rules/applicability", ClearApplicability);
        }
    }

    /// <summary>
    /// The named-rule pool as this repository resolves it: every rule with the effect of packs and
    /// overrides applied, a trace saying where each rule's state came from, the packs, and what each
    /// writable scope contributes. File paths stay inside the process; only scopes and
    /// scope-relative names are reported. A broken configuration is reported in
    /// <c>diagnostics</c> rather than failing the request, so it can be repaired here.
    /// </summary>
    private static IResult Rules(HttpContext context, string? kind, string? adapter, RepositoryRegistry registry,
        ApiSecurity security)
    {
        if (kind is not null && !Enum.TryParse<ReviewKind>(kind, true, out _))
        {
            return Results.BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported review kind",
                Detail = $"'{kind}' is not a review kind.",
            });
        }
        var store = Store(context, registry);
        return Results.Ok(View(store.Inspect(), kind?.ToLowerInvariant(), adapter, security.Identity(context)));
    }

    private static IResult Export(HttpContext context, string? scope, string? name, RepositoryRegistry registry) =>
        Guarded(() =>
        {
            var (registration, _) = Resolve(context, registry);
            var selected = Scope(scope);
            var document = Store(context, registry).Export(selected, name ?? (selected == RuleScopes.Project ? registration.Id : null));
            var fileName = $"rule-set-{Slug(document.Name ?? selected)}-{document.ExportedAt:yyyyMMdd}.json";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            return Results.Text(JsonSerializer.Serialize(document, AttackCoverageJson.Options) + "\n", "application/json");
        });

    private static IResult Import(HttpContext context, string? scope, RuleSetImportRequest request,
        RepositoryRegistry registry, ApiSecurity security) =>
        Guarded(() =>
        {
            var selected = Scope(scope);
            if (!request.DryRun && Forbidden(context, security, selected) is { } forbidden) return forbidden;
            if (request.RuleSet is not { ValueKind: JsonValueKind.Object } element)
                throw new RulePoolValidationException("The request requires a 'ruleSet' object.");
            var result = Store(context, registry).Import(selected, RulePoolStore.ParseRuleSet(element),
                request.Mode ?? "merge", request.DryRun, request.Reason ?? string.Empty, Actor(context, security));
            return Results.Ok(new
            {
                result.Valid,
                result.Applied,
                result.ModifiedSinceExport,
                result.Changes,
                diagnostics = result.Diagnostics.Select(Diagnostic).ToArray(),
                pool = View(result.Catalogue, null, null, security.Identity(context)),
            });
        });

    private static IResult Audit(HttpContext context, string? scope, int? limit, RepositoryRegistry registry) =>
        Guarded(() =>
        {
            var selected = Scope(scope);
            return Results.Ok(new
            {
                scope = selected,
                entries = Store(context, registry).ReadAudit(selected, limit ?? 100),
            });
        });

    private static IResult SetOverride(HttpContext context, string ruleId, string? scope, RuleOverrideMutationRequest request,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
        {
            FindingSeverity? severity = null;
            if (request.Severity is not null)
            {
                if (!RuleMarkdown.TryParseSeverity(request.Severity, out var parsed))
                    throw new RulePoolValidationException("Severity must be one of critical, high, medium, low, info.");
                severity = parsed;
            }
            return store.SetOverride(selected, new RuleOverride(ruleId, request.Enabled, severity, request.Reason ?? string.Empty), actor);
        });

    private static IResult RemoveOverride(HttpContext context, string ruleId, string? scope, string? reason,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.RemoveOverride(selected, ruleId, reason ?? string.Empty, actor));

    private static IResult ValidateCustomRule(HttpContext context, string? scope, CustomRuleValidationRequest request,
        RepositoryRegistry registry, ApiSecurity security)
    {
        var selected = RuleScopes.IsWritable(scope ?? RuleScopes.Project) ? scope ?? RuleScopes.Project : RuleScopes.Project;
        var content = request.Content ?? string.Empty;
        var id = RuleCatalogueResolver.PeekId(content) ?? string.Empty;
        try
        {
            var catalogue = Store(context, registry).PutCustomRule(selected, id, content, string.Empty,
                Actor(context, security), dryRun: true);
            // A retired rule (enabled: false) is valid but never resolved, so it has no view.
            var rule = catalogue.Rules.FirstOrDefault(candidate => candidate.Rule.Id == id);
            return Results.Ok(new { valid = true, id, rule = rule is null ? null : RuleView(rule), diagnostics = Array.Empty<object>() });
        }
        catch (RulePoolValidationException exception)
        {
            var diagnostics = exception.Diagnostics.Count > 0
                ? exception.Diagnostics.Select(Diagnostic).ToArray()
                : [Diagnostic(new RuleDiagnostic(selected, "custom rule", id.Length == 0 ? null : id, exception.Message))];
            return Results.Ok(new { valid = false, id, rule = (object?)null, diagnostics });
        }
    }

    private static IResult PutCustomRule(HttpContext context, string ruleId, string? scope, CustomRuleMutationRequest request,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.PutCustomRule(selected, ruleId, request.Content ?? string.Empty, request.Reason ?? string.Empty, actor));

    private static IResult DeleteCustomRule(HttpContext context, string ruleId, string? scope, string? reason,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.DeleteCustomRule(selected, ruleId, reason ?? string.Empty, actor));

    private static IResult PutPack(HttpContext context, string packId, string? scope, RulePackMutationRequest request,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
        {
            if (request.Pack is not { ValueKind: JsonValueKind.Object } element)
                throw new RulePoolValidationException("The request requires a 'pack' object.");
            return store.PutPack(selected, packId, RulePoolStore.ParsePack(element), request.Reason ?? string.Empty, actor);
        });

    private static IResult DeletePack(HttpContext context, string packId, string? scope, string? reason,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.DeletePack(selected, packId, reason ?? string.Empty, actor));

    private static IResult SetApplicability(HttpContext context, string? scope, RuleApplicabilityMutationRequest request,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.SetApplicability(selected, request.Packs ?? throw new RulePoolValidationException("The request requires a 'packs' list."),
                request.Reason ?? string.Empty, actor));

    private static IResult ClearApplicability(HttpContext context, string? scope, string? reason,
        RepositoryRegistry registry, ApiSecurity security) =>
        Mutation(context, scope, registry, security, (store, selected, actor) =>
            store.ClearApplicability(selected, reason ?? string.Empty, actor));

    private static IResult Mutation(HttpContext context, string? scope, RepositoryRegistry registry, ApiSecurity security,
        Func<RulePoolStore, string, string, ResolvedRuleCatalogue> change) =>
        Guarded(() =>
        {
            var selected = Scope(scope);
            if (Forbidden(context, security, selected) is { } forbidden) return forbidden;
            var catalogue = change(Store(context, registry), selected, Actor(context, security));
            return Results.Ok(View(catalogue, null, null, security.Identity(context)));
        });

    private static IResult Guarded(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (RulePoolValidationException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Rule configuration change rejected",
                detail: exception.Message,
                extensions: new Dictionary<string, object?> { ["diagnostics"] = exception.Diagnostics.Select(Diagnostic).ToArray() });
        }
        catch (RulePoolEntryNotFoundException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Rule pool entry not found",
                detail: exception.Message);
        }
    }

    // A global change reaches every repository on the host, so it needs the same standing as
    // registering one. A project change needs only access to the repository, which the API
    // middleware has already checked for the route.
    private static IResult? Forbidden(HttpContext context, ApiSecurity security, string scope) =>
        scope == RuleScopes.Global && !security.Identity(context).CanRegisterRepositories
            ? Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Global rule changes are not permitted")
            : null;

    private static string Scope(string? scope) => (scope ?? RuleScopes.Project).Trim().ToLowerInvariant() switch
    {
        RuleScopes.Project => RuleScopes.Project,
        RuleScopes.Global => RuleScopes.Global,
        _ => throw new RulePoolValidationException("Scope must be 'project' or 'global'."),
    };

    private static string Actor(HttpContext context, ApiSecurity security) => security.Identity(context).Id;

    private static (RepositoryRegistration Registration, RepositoryAccess Access) Resolve(HttpContext context, RepositoryRegistry registry)
    {
        var id = context.Request.RouteValues.TryGetValue("repoId", out var routeId) ? routeId?.ToString() : null;
        var registration = registry.Get(id);
        return (registration, registry.Access(registration.Id));
    }

    private static RulePoolStore Store(HttpContext context, RepositoryRegistry registry)
    {
        var (registration, repository) = Resolve(context, registry);
        var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
            ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
            : registration.GlobalInputsDirectory;
        return new RulePoolStore(repository.Root, string.IsNullOrWhiteSpace(globalDirectory) ? null : globalDirectory);
    }

    private static object View(ResolvedRuleCatalogue catalogue, string? kind, string? adapter, ApiClientIdentity identity)
    {
        var selected = catalogue.Rules
            .Where(rule => kind is null || rule.Rule.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            .Where(rule => RuleCatalogueResolver.AppliesTo(rule.Rule.Technology, adapter))
            .ToArray();
        object ScopeView(string scope, bool writable, string location)
        {
            var state = catalogue.ScopeStates[scope];
            return new
            {
                writable,
                location,
                overrides = state.Overrides.Select(entry => new
                {
                    entry.Id,
                    entry.Enabled,
                    severity = entry.Severity is { } severity ? RuleMarkdown.SeverityName(severity) : null,
                    entry.Reason,
                }).ToArray(),
                applicability = state.Applicability is null ? null : new { state.Applicability.Packs, state.Applicability.Reason },
                customRules = state.CustomRules.Select(file => new { file.Id, file.FileName, file.Content }).ToArray(),
                packs = state.Packs.Select(file => new { file.Id, file.FileName, file.Content }).ToArray(),
            };
        }
        return new
        {
            catalogueVersion = catalogue.CatalogueVersion,
            filter = new { kind, adapter },
            sources = catalogue.SourceScopes,
            valid = catalogue.IsValid,
            diagnostics = catalogue.Diagnostics.Select(Diagnostic).ToArray(),
            applicability = new { catalogue.Applicability.Scope, catalogue.Applicability.Packs, catalogue.Applicability.Reason },
            packs = catalogue.Packs.Select(pack => new
            {
                pack.Pack.Id,
                pack.Pack.Version,
                pack.Pack.Title,
                pack.Pack.Description,
                pack.Pack.ProjectTypes,
                pack.Pack.Include,
                pack.Origin,
                pack.RuleIds,
                selected = catalogue.Applicability.Packs.Contains(pack.Pack.Id, StringComparer.Ordinal),
            }).ToArray(),
            scopes = new
            {
                project = ScopeView(RuleScopes.Project, true, ".quality/rules"),
                global = ScopeView(RuleScopes.Global, identity.CanRegisterRepositories, "data root: rules"),
                sharedGlobal = ScopeView(RuleScopes.SharedGlobal, false, "global inputs: " + RuleCatalogueResolver.GlobalFileName),
            },
            rules = selected.Select(RuleView).ToArray(),
            traces = selected.Select(rule => new
            {
                rule.Rule.Id,
                source = rule.OverrideScope ?? RuleScopes.BuiltIn,
                enabled = rule.EffectiveEnabled,
                severityOverridden = rule.SeverityOverridden,
                reason = rule.OverrideReason,
                kinds = rule.Rule.Kinds,
                rule.Rule.Technology,
                adapters = new[] { "angular", "dotnet", "generic" }
                    .Where(candidate => RuleCatalogueResolver.AppliesTo(rule.Rule.Technology, candidate))
                    .ToArray(),
                rule.Origin,
                selectedBy = rule.SelectedBy ?? [],
                applicabilityScope = catalogue.Applicability.Scope,
            }).ToArray(),
        };
    }

    private static object RuleView(ResolvedRule rule) => new
    {
        rule.Rule.Id,
        rule.Rule.Version,
        rule.Rule.Title,
        rule.Rule.Technology,
        rule.Rule.Category,
        rule.Rule.Kinds,
        rule.Rule.Statement,
        rule.Rule.Rationale,
        rule.Rule.Detection,
        rule.Rule.GoodExample,
        rule.Rule.BadExample,
        severity = RuleMarkdown.SeverityName(rule.EffectiveSeverity),
        authoredSeverity = RuleMarkdown.SeverityName(rule.Rule.Severity),
        enabled = rule.EffectiveEnabled,
        rule.Rule.DefaultOn,
        rule.Rule.Autofixable,
        rule.Rule.DeterministicRuleIds,
        rule.Rule.RelatedGuideline,
        rule.Rule.Since,
        rule.Origin,
        selectedBy = rule.SelectedBy ?? [],
    };

    private static object Diagnostic(RuleDiagnostic diagnostic) => new
    {
        diagnostic.Scope,
        diagnostic.Source,
        diagnostic.Subject,
        diagnostic.Message,
    };

    private static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray())
            .Trim('-');
        return slug.Length == 0 ? "rules" : slug.Length > 60 ? slug[..60] : slug;
    }
}
