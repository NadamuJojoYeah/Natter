using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Calls every registered <see cref="IPromptFragmentProvider"/> in stable reference order. Fault
/// tolerance and fairness live here: each provider gets its own timeout and quota, and a provider
/// that throws/returns garbage/times out only loses its own fragments. Caller cancellation is
/// propagated untouched. Logs contain only reference/id/code/elapsed/count — never fragment text
/// or variable values.
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

            var providerFragments = 0;
            var providerCharacters = 0;
            var providerLimitWarned = false;
            var providerBudgetWarned = false;

            foreach (var fragment in fragments)
            {
                if (fragment is null)
                {
                    Reject(reference, null, PromptCompilerCodes.FragmentInvalid);
                    continue;
                }

                if (fragment.Source is PromptFragmentSource.HostBuiltin)
                {
                    Reject(reference, fragment.Id, PromptCompilerCodes.FragmentSourceRejected);
                    continue;
                }

                if (!PromptPluginFragmentRules.IsValidLocalId(fragment.Id)
                    || !PromptPluginFragmentRules.TryGetOwner(reference, out var owner))
                {
                    Reject(reference, fragment.Id, PromptCompilerCodes.FragmentIdRejected);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fragment.Text))
                {
                    Reject(reference, fragment.Id, PromptCompilerCodes.FragmentTextRejected);
                    continue;
                }

                if (fragment.Text.Length > _options.MaxFragmentCharacters)
                {
                    Reject(reference, fragment.Id, PromptCompilerCodes.FragmentTooLong);
                    continue;
                }

                var namespacedId = $"{owner}#{fragment.Id}";

                // Fair per-provider quotas: one plugin can only lose its own fragments.
                if (providerFragments >= Math.Max(0, _options.MaxFragmentsPerProvider))
                {
                    rejected++;
                    if (!providerLimitWarned)
                    {
                        providerLimitWarned = true;
                        warnings.Add($"{PromptCompilerCodes.PluginFragmentProviderLimit}:{reference}");
                        _logger?.LogWarning(
                            "prompt fragment provider quota reference={Reference} code={Code} limit={Limit}",
                            reference,
                            PromptCompilerCodes.PluginFragmentProviderLimit,
                            _options.MaxFragmentsPerProvider);
                    }

                    continue;
                }

                if (providerCharacters + fragment.Text.Length > Math.Max(0, _options.MaxCharactersPerProvider))
                {
                    rejected++;
                    if (!providerBudgetWarned)
                    {
                        providerBudgetWarned = true;
                        warnings.Add($"{PromptCompilerCodes.PluginFragmentProviderBudget}:{reference}");
                        _logger?.LogWarning(
                            "prompt fragment provider quota reference={Reference} code={Code} limit={Limit}",
                            reference,
                            PromptCompilerCodes.PluginFragmentProviderBudget,
                            _options.MaxCharactersPerProvider);
                    }

                    continue;
                }

                if (accepted.Count >= _options.MaxFragments)
                {
                    Reject(reference, namespacedId, PromptCompilerCodes.PluginFragmentLimit);
                    continue;
                }

                if (totalCharacters + fragment.Text.Length > _options.MaxTotalCharacters)
                {
                    Reject(reference, namespacedId, PromptCompilerCodes.FragmentBudget);
                    continue;
                }

                if (!seenIds.Add(namespacedId))
                {
                    Reject(reference, namespacedId, PromptCompilerCodes.FragmentDuplicate);
                    continue;
                }

                var maxCharacters = fragment.MaxCharacters <= 0
                    ? _options.MaxFragmentCharacters
                    : Math.Min(fragment.MaxCharacters, _options.MaxFragmentCharacters);

                accepted.Add(fragment with
                {
                    Id = namespacedId,
                    Source = PromptFragmentSource.Plugin,
                    Order = PromptPluginFragmentRules.NormalizeOrder(fragment.Order),
                    MaxCharacters = maxCharacters,
                });
                providerFragments++;
                providerCharacters += fragment.Text.Length;
                totalCharacters += fragment.Text.Length;
            }
        }

        var orderedFragments = accepted
            .OrderBy(fragment => fragment.Order)
            .ThenBy(fragment => fragment.Id, StringComparer.Ordinal)
            .ToArray();
        var elapsed = Elapsed(started);

        _logger?.LogDebug(
            "prompt fragments collected providers={ProviderCount} accepted={AcceptedCount} rejected={RejectedCount} elapsedMs={ElapsedMs}",
            ordered.Length,
            orderedFragments.Length,
            rejected,
            elapsed);

        return new PromptFragmentSourceResult(orderedFragments, warnings, ordered.Length, rejected, elapsed);

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
