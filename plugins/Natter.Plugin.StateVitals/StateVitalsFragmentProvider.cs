using Natter.Application.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.StateVitals;

/// <summary>
/// Contributes the bucketed character state as one late prompt fragment (<c>prompt.fragment</c>,
/// design §5). The fragment is a single cheap line with tags — never floats, never per-turn text —
/// so the compiled plan hash only moves when a bucket is crossed (design §4).
///
/// <para>
/// The provider never fails a turn: a disabled runtime contributes nothing, and any other failure
/// is logged (id/code only) and answered with an empty list, leaving the host prompt unchanged.
/// </para>
/// </summary>
public sealed class StateVitalsFragmentProvider : IPromptFragmentProvider
{
    private readonly StateVitalsRuntime _runtime;
    private readonly StateVitalsOptions _options;
    private readonly IPluginLog? _log;

    public StateVitalsFragmentProvider(StateVitalsRuntime runtime, StateVitalsOptions options, IPluginLog? log = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    public async ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
        PromptFragmentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_runtime.Enabled)
            return [];

        try
        {
            var text = await _runtime.RenderAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return [];

            // The conversation scope from PromptFragmentContext is intentionally unused in v1:
            // the character state is global, so per-conversation facts must not leak into it (§7.2).
            return
            [
                new PromptFragment(
                    StateVitalsDefaults.FragmentId,
                    text,
                    PromptFragmentSource.Plugin,
                    Order: _options.FragmentOrder,
                    Version: _runtime.Packs?.DefinitionVersion),
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // ids and a code only: fragment text never enters logs (design §6.4).
            _log?.Warning(
                "state fragment skipped code={0} error={1}",
                StateVitalsCodes.FragmentSkipped,
                exception.GetType().Name);

            return [];
        }
    }
}
