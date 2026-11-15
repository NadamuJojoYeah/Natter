using Natter.Cli.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Host CLI contributor backed by activated NPS cli.command contributions.</summary>
internal sealed class NpsCliCommandContributor : ICliCommandContributor
{
    private readonly NpsPluginRuntime _runtime;

    public NpsCliCommandContributor(NpsPluginRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public IEnumerable<CliCommandDescriptor> GetCommands() => _runtime.GetCliCommands();
}
