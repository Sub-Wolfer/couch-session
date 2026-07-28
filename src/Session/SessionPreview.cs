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
    /// <summary>
    /// One thing a session does, and what becomes of it at the end.
    ///
    /// [BUG] <c>After</c> used to be a three-way enum the dialog turned into the words "put back
    /// after" or "stays after" for every row alike. Two problems with that. It is abstract — put
    /// *what* back — and on the display row it was simply wrong: nothing about moving the picture
    /// to the television is put back, and the television in fact disconnects. What comes back is
    /// the desktop, which is a different sentence.
    ///
    /// Each step now says what happens to it when the session ends, in its own words. Reversed is
    /// only kept for the colour, since "this returns to how it was" and "this does not" is still
    /// worth telling apart at a glance.
    /// </summary>
    public sealed record Step(string What, string Detail, string After, bool Reversed);

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
                           string.Join(" · ", detail), Ui.Words.PreviewAfterDisplay, true));
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
                           Ui.Words.PreviewAfterAudio, true));
    }

    private static void Hdr(AppConfig config, List<Step> steps)
    {
        switch (config.HdrSwitching)
        {
            case HdrMode.WholeSession:
                steps.Add(new Step(Ui.Words.PreviewHdrSession, "", Ui.Words.PreviewAfterHdr, true));
                break;

            case HdrMode.PerGame:
                int ticked = config.HdrGames.Count;
                if (ticked == 0) break;   // on, but nothing is ticked, so nothing will happen

                steps.Add(new Step(Ui.Words.PreviewHdrPerGame,
                                   string.Format(Ui.Words.PreviewHdrGames, ticked),
                                   Ui.Words.PreviewAfterHdrGame, true));
                break;
        }
    }

    private static void BigPicture(AppConfig config, List<Step> steps)
    {
        if (!Steam.BigPictureLauncher.IsSteamInstalled()) return;

        steps.Add(new Step(Ui.Words.PreviewBigPicture,
                           config.RestartSteamForAudio ? Ui.Words.PreviewBigPictureRestart : "",
                           Ui.Words.PreviewAfterBigPicture, true));
    }

    private static void Performance(AppConfig config, List<Step> steps)
    {
        if (config.ChangePowerPlan)
        {
            string detail = config.PowerPlanOnlyWhenPluggedIn && PowerLine.Now() == PowerLine.State.OnBattery
                                ? Ui.Words.PreviewPowerSkipped
                                : "";

            steps.Add(new Step(Ui.Words.PreviewPowerPlan, detail,
                               detail.Length > 0 ? "" : Ui.Words.PreviewAfterPower,
                               detail.Length == 0));
        }

        if (config.SilenceNotifications)
            steps.Add(new Step(Ui.Words.PreviewNotifications, "", Ui.Words.PreviewAfterNotifications, true));

        if (config.GamePriorityEnabled)
            steps.Add(new Step(Ui.Words.PreviewPriority, "", Ui.Words.PreviewAfterPriority, true));
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
                           config.ReopenAppsAfterSession ? Ui.Words.PreviewAfterAppsBack
                                                         : Ui.Words.PreviewAfterAppsStay,
                           config.ReopenAppsAfterSession));
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
