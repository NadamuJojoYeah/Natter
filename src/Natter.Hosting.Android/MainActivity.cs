using System.Text;
using Android.App;
using Android.OS;
using Android.Graphics;
using Android.Widget;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Store;

namespace Natter.Hosting.Android;

/// <summary>
/// Android head for the Natter CLI. It reuses the exact same CLI kernel and composition
/// root as the desktop host; plugins that ship as DLLs on desktop are registered as
/// embedded modules because an APK cannot scan a plugin directory.
/// </summary>
[Activity(Label = "Natter CLI", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var output = new TextView(this)
        {
            TextSize = 11f,
            Typeface = Typeface.Monospace,
        };
        output.SetTextIsSelectable(true);

        var layout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        layout.SetPadding(32, 64, 32, 32);
        layout.AddView(output);

        var scroll = new ScrollView(this);
        scroll.AddView(layout);
        SetContentView(scroll);

        var console = new AndroidConsole(this, output);
        _ = RunCliAsync(console);
    }

    private static async Task RunCliAsync(AndroidConsole console)
    {
        console.Out.WriteLine("Natter • Android host");
        console.Out.WriteLine("---------------------");

        try
        {
            var options = new NatterCliOptions();
            PluginStore? store = null;

            if (AndroidPluginHost.IsDynamic)
            {
                store = AndroidPluginHost.CreateStore(global::Android.App.Application.Context);
                await store.CleanupAsync();

                foreach (var plugin in store.GetInstalledPlugins())
                {
                    console.Out.WriteLine(
                        $"[plugin] {plugin.Id} {plugin.ActiveVersion} enabled={plugin.Enabled} state={plugin.LastKnownState}");
                }
            }

            var args = AndroidPluginHost.IsDynamic
                ? new[] { "exec", "plugins list --detail", "plugins graph", "plugins contributions" }
                : new[] { "exec", "doctor", "plugins list --detail", "demo --times 2", "metrics", "greetings recent" };

            var exitCode = await NatterCli.RunAsync(
                args,
                console,
                options,
                builder => AndroidPluginHost.Configure(global::Android.App.Application.Context, options, builder, store));

            console.Out.WriteLine($"exit code: {exitCode}");
        }
        catch (Exception ex)
        {
            console.Error.WriteLine(ex.ToString());
        }
    }
}

/// <summary>IConsole implementation that mirrors output into an Android TextView.</summary>
internal sealed class AndroidConsole : IConsole
{
    private readonly Activity _activity;
    private readonly TextView _view;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public AndroidConsole(Activity activity, TextView view)
    {
        _activity = activity;
        _view = view;
        Out = new TextViewWriter(this);
        Error = new TextViewWriter(this);
    }

    public TextWriter Out { get; }

    public TextWriter Error { get; }

    public bool IsInteractive => false;

    public string? ReadLine() => null;

    internal void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        string snapshot;
        lock (_gate)
        {
            _buffer.Append(text);
            snapshot = _buffer.ToString();
        }

        _activity.RunOnUiThread(() => _view.Text = snapshot);
    }

    private sealed class TextViewWriter : TextWriter
    {
        private readonly AndroidConsole _owner;

        public TextViewWriter(AndroidConsole owner) => _owner = owner;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _owner.Append(value.ToString());

        public override void Write(string? value) => _owner.Append(value);

        public override void WriteLine(string? value) => _owner.Append(value + System.Environment.NewLine);

        public override void WriteLine() => _owner.Append(System.Environment.NewLine);
    }
}
