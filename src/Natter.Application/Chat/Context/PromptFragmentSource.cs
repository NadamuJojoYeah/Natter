using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Rules for plugin-contributed prompt fragments. Plugins are guests: they may add fragments but
/// never force them (no mandatory), never impersonate host builtins, and must use a narrow id
/// format so host configuration cannot collide by accident.
/// </summary>
public static class PromptPluginFragmentRules
{
    public const string IdPattern = "^[a-z0-9][a-z0-9._-]{0,63}$";

    private static readonly Regex IdRegex = new(
        IdPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly IReadOnlySet<string> HostReservedIds = new HashSet<string>(
        HostPromptSafety.Fragments.Select(fragment => fragment.Id)
            .Append(HostStaticPromptProvider.SystemFragmentId),
        StringComparer.Ordinal);

    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && IdRegex.IsMatch(id);

    public static bool IsHostReservedId(string id) => HostReservedIds.Contains(id);

    public static int NormalizeOrder(int order)
        => Math.Clamp(order, 0, PromptCompilerDefaults.PluginFragmentOrderMax);
}

/// <summary>Limits and timeout for the host fragment aggregator.</summary>
public sealed record PromptFragmentSourceOptions(
    TimeSpan ProviderTimeout,
    int MaxFragments,
    int MaxFragmentCharacters,
    int MaxTotalCharacters)
{
    public static PromptFragmentSourceOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(PromptCompilerDefaults.PluginFragmentTimeoutMilliseconds),
        PromptCompilerDefaults.PluginMaxFragments,
        PromptCompilerDefaults.FragmentMaxCharacters,
        PromptCompilerDefaults.PluginTotalMaxCharacters);
}

/// <summary>Cleaned plugin fragments plus diagnostics (no fragment text is ever included).</summary>
public sealed record PromptFragmentSourceResult(
    IReadOnlyList<PromptFragment> Fragments,
    IReadOnlyList<string> Warnings,
    int ProviderCount,
    int RejectedCount)
{
    public static PromptFragmentSourceResult Empty { get; } = new([], [], 0, 0);
}

/// <summary>Host-side aggregation point for <c>prompt.fragment</c> providers.</summary>
public interface IPromptFragmentSource
{
    ValueTask<PromptFragmentSourceResult> GetFragmentsAsync(
        PromptFragmentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Builds the target facts handed to plugin fragment providers.</summary>
public static class PromptFragmentContexts
{
    public static PromptFragmentContext FromPromptRequest(PromptRequest request, string? presetId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Profile.Prompt.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;
        foreach (var pair in request.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;

        return new PromptFragmentContext(
            request.Profile,
            presetId,
            request.Locale ?? request.Profile.Prompt.Locale,
            request.Source.Platform,
            request.Source.Protocol,
            request.Conversation.Kind,
            variables);
    }
}

/// <summary>
/// Calls every registered <see cref="IPromptFragmentProvider"/> in stable reference order. Fault
/// tolerance lives here: a provider that throws/returns garbage/times out is dropped with a warning
/// and never fails the prompt. Caller cancellation is always propagated. Logs contain only
/// reference/id/code/elapsed/count — never fragment text or variable values.
/// </summary>
public sealed class HostPromptFragmentSource : IPromptFragmentSource
{
    private readonly IContextComponentCatalog _catalog;
    private readonly ILogger<HostPromptFragmentSource>? _logger;
    private readonly PromptFragmentSourceOptions _options;

    public HostPromptFragmentSource(
        IContextComponentCatalog catalog,
        ILogger<HostPromptFragmentSource>? logger = null,
        PromptFragmentSourceOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;
        _options = options ?? PromptFragmentSourceOptions.Default;
    }

    public async ValueTask<PromptFragmentSourceResult> GetFragmentsAsync(
        PromptFragmentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var providers = _catalog.FragmentProviders;
        if (providers.Count == 0)
            return PromptFragmentSourceResult.Empty;

        var ordered = providers
            .OrderBy(provider => provider.Reference, StringComparer.Ordinal)
            .ToArray();
        var warnings = new List<string>();
        var accepted = new List<PromptFragment>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;
        var rejected = 0;
        var started = Stopwatch.GetTimestamp();

        foreach (var (reference, provider) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var providerStarted = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.ProviderTimeout > TimeSpan.Zero)
                timeout.CancelAfter(_options.ProviderTimeout);

            IReadOnlyList<PromptFragment>? fragments;
            try
            {
                // WaitAsync enforces the per-provider budget even when a provider ignores the
                // cancellation token: the host stops waiting and the abandoned task is not awaited.
                fragments = await provider.GetFragmentsAsync(context, timeout.Token)
                    .AsTask()
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation is not a plugin failure: propagate it untouched.
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                warnings.Add($"{PromptCompilerCodes.FragmentTimeout}:{reference}");
                _logger?.LogWarning(
                    "prompt fragment provider timeout reference={Reference} code={Code} elapsedMs={ElapsedMs}",
                    reference,
                    PromptCompilerCodes.FragmentTimeout,
                    Elapsed(providerStarted));
                continue;
            }
            catch (Exception)
            {
                warnings.Add($"{PromptCompilerCodes.FragmentFailed}:{reference}");
                _logger?.LogWarning(
                    "prompt fragment provider failed reference={Reference} code={Code} elapsedMs={ElapsedMs}",
                    reference,
                    PromptCompilerCodes.FragmentFailed,
                    Elapsed(providerStarted));
                continue;
            }

            if (fragments is null)
            {
                warnings.Add($"{PromptCompilerCodes.FragmentFailed}:{reference}");
                _logger?.LogWarning(
                    "prompt fragment provider returned null reference={Reference} code={Code} elapsedMs={ElapsedMs}",
                    reference,
                    PromptCompilerCodes.FragmentFailed,
                    Elapsed(providerStarted));
                continue;
            }

            foreach (var fragment in fragments)
            {
                if (fragment is null)
                {
                    Reject(reference, null, PromptCompilerCodes.FragmentInvalid);
                    continue;
                }

                var id = fragment.Id ?? string.Empty;
                if (fragment.Mandatory)
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentMandatoryRejected);
                    continue;
                }

                if (fragment.Source is PromptFragmentSource.HostBuiltin)
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentSourceRejected);
                    continue;
                }

                if (!PromptPluginFragmentRules.IsValidId(id))
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentIdRejected);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fragment.Text))
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentTextRejected);
                    continue;
                }

                if (fragment.Text.Length > _options.MaxFragmentCharacters)
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentTooLong);
                    continue;
                }

                if (PromptPluginFragmentRules.IsHostReservedId(id))
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentConflict);
                    continue;
                }

                if (seenIds.Contains(id))
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentDuplicate);
                    continue;
                }

                if (accepted.Count >= _options.MaxFragments)
                {
                    Reject(reference, id, PromptCompilerCodes.PluginFragmentLimit);
                    continue;
                }

                if (totalCharacters + fragment.Text.Length > _options.MaxTotalCharacters)
                {
                    Reject(reference, id, PromptCompilerCodes.FragmentBudget);
                    continue;
                }

                var maxCharacters = fragment.MaxCharacters <= 0
                    ? _options.MaxFragmentCharacters
                    : Math.Min(fragment.MaxCharacters, _options.MaxFragmentCharacters);

                accepted.Add(fragment with
                {
                    Source = PromptFragmentSource.Plugin,
                    Order = PromptPluginFragmentRules.NormalizeOrder(fragment.Order),
                    CanDisable = true,
                    Mandatory = false,
                    MaxCharacters = maxCharacters,
                });
                seenIds.Add(id);
                totalCharacters += fragment.Text.Length;
            }
        }

        var orderedFragments = accepted
            .OrderBy(fragment => fragment.Order)
            .ThenBy(fragment => fragment.Id, StringComparer.Ordinal)
            .ToArray();

        _logger?.LogDebug(
            "prompt fragments collected providers={ProviderCount} accepted={AcceptedCount} rejected={RejectedCount} elapsedMs={ElapsedMs}",
            ordered.Length,
            orderedFragments.Length,
            rejected,
            Elapsed(started));

        return new PromptFragmentSourceResult(orderedFragments, warnings, ordered.Length, rejected);

        void Reject(string reference, string? id, string code)
        {
            rejected++;
            warnings.Add(string.IsNullOrWhiteSpace(id) ? $"{code}:{reference}" : $"{code}:{id}");
            _logger?.LogWarning(
                "prompt fragment rejected reference={Reference} id={Id} code={Code}",
                reference,
                string.IsNullOrWhiteSpace(id) ? "-" : id,
                code);
        }
    }

    private static long Elapsed(long started)
        => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
