using CouchMode.Audio;
using CouchMode.Display;

namespace CouchMode.Session;

/// <summary>
/// What pressing Start would actually do, worked out from the settings as they stand.
///
/// This app changes things outside itself — the display topology, the default sound device, HDR, the
/// power plan, and now other people's open applications. Every one of those is described on its own
/// row on some page, and nobody reads seven pages before pressing a button. The result is that the
/// first honest answer to "what is this going to do to my machine" arrives after it has done it.
///
/// So it is answered beforehand instead, in one list, built from the same config the session reads.
/// Two rules make it worth trusting:
///
///   - **Only what will really happen.** A step whose setting is off does not appear. A display that
///     has not been chosen produces a problem, not a step.
///   - **Whether it is put back.** That is the question underneath the question, and it is the one
///     thing a settings page spread over seven tabs is worst at answering.
/// </summary>
public static class SessionPreview
{
    /// <summary>Whether a step undoes itself when the session ends.</summary>
    public enum Undo
    {
        /// <summary>Put back when the session ends.</summary>
        Restored,

        /// <summary>Left as it is afterwards, deliberately.</summary>
        Kept,

        /// <summary>Nothing to undo.</summary>
        None,
    }

    public sealed record Step(string What, string Detail, Undo Restores);

    /// <summary>Something that would stop a session, or make one useless. Empty when all is well.</summary>
    public sealed record Problem(string What, string Detail);

    public sealed record Result(IReadOnlyList<Step> Steps, IReadOnlyList<Problem> Problems);

    public static Result For(AppConfig config)
    {
        var steps = new List<Step>();
        var problems = new List<Problem>();

        Display(config, steps, problems);
        Audio(config, steps, problems);
        Hdr(config, steps);
        BigPicture(config, steps);
        Performance(config, steps);
        Apps(config, steps, problems);

        return new Result(steps, problems);
    }

    private static void Display(AppConfig config, List<Step> steps, List<Problem> problems)
    {
        if (config.TvDisplayPath.Length == 0)
        {
            problems.Add(new Problem(Ui.Words.PreviewNoDisplay, Ui.Words.PreviewNoDisplayWhy));
            return;
        }

        string name = NameOfDisplay(config.TvDisplayPath);
        var detail = new List<string>();

        if (config.TvVideoMode.Length > 0) detail.Add(config.TvVideoMode);
        detail.Add(config.DisplayMode == TvDisplayMode.TvOnly
                       ? Ui.Words.PreviewTvOnly
                       : Ui.Words.PreviewKeepMonitors);

        steps.Add(new Step(string.Format(Ui.Words.PreviewDisplay, name),
                           string.Join(" · ", detail), Undo.Restored));
    }

    private static void Audio(AppConfig config, List<Step> steps, List<Problem> problems)
    {
        if (!config.SwitchAudio) return;

        if (config.TvAudioDeviceId.Length == 0)
        {
            problems.Add(new Problem(Ui.Words.PreviewNoAudio, Ui.Words.PreviewNoAudioWhy));
            return;
        }

        steps.Add(new Step(string.Format(Ui.Words.PreviewAudio, NameOfAudio(config.TvAudioDeviceId)),
                           config.RestartSteamForAudio ? Ui.Words.PreviewAudioSteam : "",
                           Undo.Restored));
    }

    private static void Hdr(AppConfig config, List<Step> steps)
    {
        switch (config.HdrSwitching)
        {
            case HdrMode.WholeSession:
                steps.Add(new Step(Ui.Words.PreviewHdrSession, "", Undo.Restored));
                break;

            case HdrMode.PerGame:
                int ticked = config.HdrGames.Count;
                if (ticked == 0) break;   // on, but nothing is ticked, so nothing will happen

                steps.Add(new Step(Ui.Words.PreviewHdrPerGame,
                                   string.Format(Ui.Words.PreviewHdrGames, ticked), Undo.Restored));
                break;
        }
    }

    private static void BigPicture(AppConfig config, List<Step> steps)
    {
        if (!Steam.BigPictureLauncher.IsSteamInstalled()) return;

        steps.Add(new Step(Ui.Words.PreviewBigPicture,
                           config.RestartSteamForAudio ? Ui.Words.PreviewBigPictureRestart : "",
                           Undo.Restored));
    }

    private static void Performance(AppConfig config, List<Step> steps)
    {
        if (config.ChangePowerPlan)
        {
            string detail = config.PowerPlanOnlyWhenPluggedIn && PowerLine.Now() == PowerLine.State.OnBattery
                                ? Ui.Words.PreviewPowerSkipped
                                : "";

            steps.Add(new Step(Ui.Words.PreviewPowerPlan, detail,
                               detail.Length > 0 ? Undo.None : Undo.Restored));
        }

        if (config.SilenceNotifications)
            steps.Add(new Step(Ui.Words.PreviewNotifications, "", Undo.Restored));

        if (config.GamePriorityEnabled)
            steps.Add(new Step(Ui.Words.PreviewPriority, "", Undo.Restored));
    }

    private static void Apps(AppConfig config, List<Step> steps, List<Problem> problems)
    {
        if (!config.CloseAppsForSession || config.AppsToClose.Count == 0) return;

        var names = config.AppsToClose
                          .Select(p => { try { return Path.GetFileNameWithoutExtension(p); } catch { return p; } })
                          .Where(n => n.Length > 0)
                          .ToList();

        steps.Add(new Step(string.Format(Ui.Words.PreviewCloseApps, names.Count),
                           string.Join(", ", names),
                           config.ReopenAppsAfterSession ? Undo.Restored : Undo.Kept));
    }

    private static string NameOfDisplay(string path)
    {
        try
        {
            var found = DisplayManager.ListDisplays().FirstOrDefault(d => d.DevicePath == path);
            return found?.FriendlyName ?? Ui.Words.PreviewUnknownDevice;
        }
        catch { return Ui.Words.PreviewUnknownDevice; }
    }

    private static string NameOfAudio(string id)
    {
        try
        {
            var found = AudioManager.ListPlaybackDevicesIncludingOff().FirstOrDefault(d => d.Id == id);
            return found?.FriendlyName ?? Ui.Words.PreviewUnknownDevice;
        }
        catch { return Ui.Words.PreviewUnknownDevice; }
    }
}
