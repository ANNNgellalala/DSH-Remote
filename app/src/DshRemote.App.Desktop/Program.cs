using Avalonia;
using DshRemote.App.Services;

namespace DshRemote.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Desktop convenience: the relay plugin writes %USERPROFILE%\.dsh\remote\pairing.json and the
        // connect page can load it instead of pasting the URI. Read-only, never modified.
        AppHost.PairingFilePathsProvider = () => new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh",
                "remote",
                "pairing.json")
        };

        // TODO(integration): once DshRemote.App references the real client constructor needs, wire it here:
        // AppHost.ClientFactory = () => new DshRemote.Core.DshClient();

        // Dev-only headless walkthrough used for screenshots/manual smoke checks:
        //   DshRemote.App.Desktop.exe --demo | --demo-chat | --demo-chat-send
        //   DshRemote.App.Desktop.exe --open-first --pair-uri=...   (real client, opens session 0)
        //   DshRemote.App.Desktop.exe --open-first-send --pair-uri=...  (also sends one prompt)
        AppHost.StartupAction = args
            .FirstOrDefault(argument =>
                argument is "--demo" or "--demo-chat" or "--demo-chat-send"
                    or "--demo-chat-approval" or "--demo-chat-question" or "--demo-chat-steer"
                    or "--open-first" or "--open-first-send")
            ?.TrimStart('-');

        var indexArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--open-index=", StringComparison.OrdinalIgnoreCase));
        if (indexArgument is not null && int.TryParse(indexArgument["--open-index=".Length..], out var openIndex))
        {
            AppHost.StartupOpenIndex = openIndex;
        }

        // Long screenshot automation probe: opens the demo chat, runs the production capture path and
        // writes <path> (+ .report.txt), then exits 0. Optional budget overrides exercise the split path:
        //   --longshot-probe=D:\_work\probe.png [--longshot-max-edge=1200] [--longshot-max-pixels=2000000]
        var longShotProbeArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--longshot-probe=", StringComparison.OrdinalIgnoreCase));
        if (longShotProbeArgument is not null)
        {
            AppHost.LongShotProbeOutputPath = longShotProbeArgument["--longshot-probe=".Length..].Trim('"');
            AppHost.LongShotProbeLimits = BuildLongShotProbeLimits(args);
            AppHost.StartupAction = "longshot-probe";
        }

        // E2E / smoke: connect straight to a desktop runtime, e.g.
        //   --pair-uri="dshremote://pair?v=1&host=127.0.0.1&port=18090&token=..."
        // or set DSHREMOTE_PAIR_URI in the environment.
        var pairArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--pair-uri=", StringComparison.OrdinalIgnoreCase));
        AppHost.StartupPairingUri = pairArgument is null
            ? Environment.GetEnvironmentVariable("DSHREMOTE_PAIR_URI")
            : pairArgument["--pair-uri=".Length..];

        var deepLinkArgument = args.FirstOrDefault(argument =>
            argument.StartsWith("--deep-link=", StringComparison.OrdinalIgnoreCase));
        if (deepLinkArgument is not null)
        {
            AppHost.StartupDeepLink = deepLinkArgument["--deep-link=".Length..];
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<DshRemote.App.App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Parses the optional long-screenshot probe budget overrides. Returns null when none were given so
    /// the probe uses <c>LongShotLimits.Default</c>.
    /// </summary>
    private static DshRemote.App.Services.LongShotLimits? BuildLongShotProbeLimits(string[] args)
    {
        int? Read(string name)
        {
            var argument = args.FirstOrDefault(value =>
                value.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            if (argument is not null &&
                int.TryParse(argument[(name.Length + 1)..], out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            return null;
        }

        var maxEdge = Read("--longshot-max-edge");
        var maxPixels = Read("--longshot-max-pixels");
        var minWidth = Read("--longshot-min-width");
        var maxImages = Read("--longshot-max-images");
        if (maxEdge is null && maxPixels is null && minWidth is null && maxImages is null)
        {
            return null;
        }

        var defaults = DshRemote.App.Services.LongShotLimits.Default;
        return new DshRemote.App.Services.LongShotLimits
        {
            MaxEdgePixels = maxEdge ?? defaults.MaxEdgePixels,
            MaxPixelsPerImage = maxPixels ?? defaults.MaxPixelsPerImage,
            MinWidthPixels = minWidth ?? defaults.MinWidthPixels,
            MaxImages = maxImages ?? defaults.MaxImages
        };
    }
}
