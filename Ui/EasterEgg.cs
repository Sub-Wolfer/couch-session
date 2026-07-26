namespace CouchMode.Ui;

/// <summary>
/// The easter egg: a fullscreen overlay baked into the exe, summoned by a secret combination on the
/// session prompt.
///
/// Deliberately built on nothing. An animated GIF is played by WinForms itself — PictureBox animates one
/// natively — so this needs no media framework, no COM, no extra file beside the exe, and nothing that
/// could fail to load in a single-file build or look interesting to an anti-cheat. A real video codec
/// would drag in a media stack for a joke, which is a bad trade in an app whose main requirement is that
/// it never gets anyone banned.
///
/// There is no setting for it and nothing in the UI mentions it, which is rather the point.
/// </summary>
internal static class EasterEgg
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy,
                                            uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;

    /// <summary>
    /// The embedded resource name, set in the csproj alongside the icon.
    ///
    /// Baked into the exe rather than read from the data folder. A file sitting next to the config is
    /// not an easter egg — it is a feature with a hidden switch, discoverable by anyone who opens the
    /// folder, and deletable by anyone who tidies it. This ships inside the binary, so the only way to
    /// find it is to know the combination.
    /// </summary>
    private const string Resource = "easteregg.gif";

    /// <summary>The soundtrack, embedded the same way. WAV, because SoundPlayer is built into .NET and
    /// plays one from a stream — an mp3 would mean dragging in a media stack for a joke.</summary>
    private const string SoundResource = "easteregg.wav";

    private static Form? _open;

    /// <summary>
    /// Whether the egg is on screen.
    ///
    /// The session prompt asks before defending its own place at the front. That prompt re-states itself
    /// as topmost several times a second — it has to, or Steam's overlay steals the controller — and
    /// without this it would immediately cover the thing it just summoned.
    /// </summary>
    public static bool IsPlaying => _open is { IsDisposed: false };

    /// <summary>How long it stays up when nobody touches anything.</summary>
    private static readonly TimeSpan Runs = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Take it down, from anywhere, safely, however many times you like.
    ///
    /// Exists because a window that will not go away is the worst outcome here by a wide margin — this
    /// is a joke, and a joke stuck over someone's television until they kill the app is not one. The
    /// timer inside is the ordinary way it ends; this is the guarantee that does not depend on it.
    /// </summary>
    public static void Stop()
    {
        var form = _open;
        if (form is null || form.IsDisposed) { _open = null; return; }

        try
        {
            if (form.InvokeRequired) form.BeginInvoke(form.Close);
            else form.Close();
        }
        catch { /* already going */ }

        _open = null;
    }

    /// <summary>
    /// The embedded clip, decoded fresh each time.
    ///
    /// Not cached: it is a few seconds long and summoned rarely, so holding a decoded image in memory
    /// for the life of the process to save a few milliseconds once in a blue moon is the wrong trade.
    /// Null when the build did not include it, which keeps this harmless if the file is ever dropped
    /// from the project.
    /// </summary>
    /// <summary>
    /// An embedded resource as a rewound MemoryStream, or null when it was not built in.
    ///
    /// Copied out of the assembly rather than handed over directly: both consumers keep the stream
    /// alive after this returns — Image reads frames from it for the life of the animation, SoundPlayer
    /// reads it on a background thread — and a resource stream disposed underneath either is a crash
    /// waiting to happen. Matched on the end of the name, because the SDK prefixes resources with the
    /// assembly name and that has already changed twice with the app's renames.
    /// </summary>
    private static MemoryStream? Embedded(string resource)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            var all = assembly.GetManifestResourceNames();
            var name = Array.Find(all, n => n.EndsWith(resource, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                // Named, with what is actually in the binary beside it. "Nothing happened" is the worst
                // possible answer for something with no UI: this says whether the file missed the build
                // entirely, which is by far the likeliest reason and invisible from the outside.
                Log.Info($"Easter egg: '{resource}' is not embedded in this build. "
                       + $"Resources present: {string.Join(", ", all)}");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;

            var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            return memory;
        }
        catch (Exception ex)
        {
            Log.Warn($"Easter egg resource '{resource}' could not be read: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Fix a WAV whose RIFF header does not know how long it is.
    ///
    /// An encoder writing to a pipe cannot seek back to fill in the size once it has finished, so it
    /// leaves 0xFFFFFFFF there — ffmpeg does this routinely, and the file plays perfectly in every media
    /// player because they all ignore the field and read to the end. SoundPlayer does not: it trusts the
    /// header, finds a length that cannot be right, and quietly plays nothing. That is the whole reason
    /// a perfectly good soundtrack was silent.
    ///
    /// The audio itself is untouched. This only writes the two lengths that were never filled in, into
    /// our own copy in memory, so any WAV that plays elsewhere plays here too.
    /// </summary>
    private static void RepairRiffSizes(MemoryStream wav)
    {
        try
        {
            var b = wav.GetBuffer();
            int length = (int)wav.Length;

            if (length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return;

            // RIFF size is everything after the first eight bytes.
            if (BitConverter.ToUInt32(b, 4) == uint.MaxValue)
            {
                BitConverter.TryWriteBytes(b.AsSpan(4, 4), (uint)(length - 8));
                Log.Info("Easter egg: the sound file had no length in its header; filled it in.");
            }

            // Then the data chunk, walking the chunk list to find it.
            int i = 12;
            while (i + 8 <= length)
            {
                uint size = BitConverter.ToUInt32(b, i + 4);

                if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a')
                {
                    if (size == uint.MaxValue || i + 8 + size > length)
                        BitConverter.TryWriteBytes(b.AsSpan(i + 4, 4), (uint)(length - i - 8));

                    return;
                }

                if (size == uint.MaxValue) return;          // unreadable from here on
                i += 8 + (int)size + ((size & 1) == 1 ? 1 : 0);   // chunks are word-aligned
            }
        }
        catch { /* leave it exactly as it was and let SoundPlayer say no */ }

        finally { wav.Position = 0; }
    }

    private static Image? Load()
    {
        try
        {
            var memory = Embedded(Resource);
            return memory is null ? null : Image.FromStream(memory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Easter egg could not be loaded: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Put it up on the given screen until any controller button is pressed, or fifteen seconds pass.
    ///
    /// Ending on the pad is the point — the whole app is used from a sofa, and "press anything to get on
    /// with it" is what a person there will try. The timeout is only a backstop for nobody being there:
    /// the prompt underneath decides "keep playing" after a few minutes of silence and closes, and a
    /// joke left running on a television because someone walked off stops being a joke. A click, a key
    /// or a second summoning all end it too.
    /// </summary>
    public static void Play(Screen? screen)
    {
        if (_open is { IsDisposed: false }) { _open.Close(); return; }

        var image = Load();
        if (image is null) return;

        var area = (screen ?? Screen.PrimaryScreen)?.Bounds ?? new Rectangle(0, 0, 1920, 1080);

        // Magenta as the see-through colour, not black.
        //
        // TransparencyKey makes one colour vanish, taking the mouse with it — so whatever is behind the
        // window stays visible *and* clickable. The colour therefore has to be one the clip will never
        // contain, or holes appear in the picture: pure magenta is the traditional choice precisely
        // because nothing real is ever exactly that.
        //
        // This is per-window transparency the supported way. The alternative, a layered window with a
        // real alpha channel, means drawing the frames by hand and gets no better a result here.
        //
        // Worth knowing at this size: chroma keying is all-or-nothing per pixel, so scaling up softens
        // the clip's edges and any half-transparent pixel around them turns into a magenta fringe rather
        // than fading out. A clip with hard edges enlarges cleanly; a soft-edged one will show it.
        var chroma = Color.Magenta;

        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = area,
            BackColor = chroma,
            TransparencyKey = chroma,
            TopMost = true,
            ShowInTaskbar = false,
            KeyPreview = true,
        };

        // As big as the screen will take it, without distorting it.
        //
        // Zoom scales the clip up until it meets the screen on its narrower axis and letterboxes the
        // rest — and those bars are the chroma colour, so they are transparent rather than black. That
        // is the difference between Zoom and Stretch here: Stretch would fill every pixel by pulling the
        // picture out of shape, while this fills the screen as far as the clip's own proportions allow
        // and lets you see straight through the remainder.
        var view = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = chroma,
            Image = image,
        };

        form.Controls.Add(view);

        void Close(object? _, EventArgs __) => form.Close();

        form.Click += Close;
        view.Click += Close;
        form.KeyDown += Close;

        // Any controller button ends it. Read from the pad directly rather than waiting for a keypress
        // to arrive, so it works regardless of which window has the foreground — the same reading the
        // prompt itself uses, and the reason this responds to a pad that Windows is routing elsewhere.
        // The soundtrack. Absent from the build, or not a WAV Windows will take, and the egg is silent —
        // never a reason to skip the picture.
        System.Media.SoundPlayer? song = null;
        var tune = Embedded(SoundResource);

        if (tune is not null)
        {
            try
            {
                RepairRiffSizes(tune);

                song = new System.Media.SoundPlayer(tune);

                // Loaded before playing, and deliberately not with Play() alone.
                //
                // Play() hands the stream to a background thread and returns before anything has been
                // parsed, so a file that is not really a WAV — an mp3 with the extension changed is the
                // usual culprit, since SoundPlayer handles PCM WAV only — fails silently out there
                // where nothing can catch it. Load() does the parsing here, on this thread, where a bad
                // file throws and can be reported instead of just never making a sound.
                song.Load();
                song.Play();

                Log.Info($"Easter egg: playing {tune.Length / 1024}KB of audio.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Easter egg sound would not play — is it a PCM .wav? ({ex.Message})");
                song?.Dispose();
                song = null;
                tune.Dispose();
                tune = null;
            }
        }

        // The watchdog runs on a pool thread, not on a WinForms timer.
        //
        // It was a WinForms timer, and the log caught that being a mistake: the egg had to be closed by
        // the backstop instead, which can only happen if the timer never ticked — and a timer that
        // never ticks is also a timer that never checks the controller, which is exactly why pressing a
        // button did nothing. A WinForms timer only fires while the thread that made it is pumping
        // messages, and this window is summoned from a prompt that is busy holding itself in front of a
        // game. Nothing here owes anything to the message loop now: the wall clock and the pad are both
        // read on a pool thread, and the only thing marshalled back is the close itself.
        var opened = DateTime.UtcNow;

        // Taken here, on the thread that owns the window, so the watchdog never touches the Form.
        IntPtr hwnd = form.Handle;

        _ = Task.Run(async () =>
        {
            var lastRaise = DateTime.MinValue;

            while (true)
            {
                await Task.Delay(60).ConfigureAwait(false);

                // Gone by any other route — a click, a key, the session ending.
                if (_open is not { IsDisposed: false } still || !ReferenceEquals(still, form)) return;

                var up = DateTime.UtcNow - opened;

                if (up >= Runs)
                {
                    Log.Info("Easter egg: time is up; closing.");
                    Stop();
                    return;
                }

                // Stay in front of the prompt that summoned it, which re-states itself as topmost
                // several times a second. SetWindowPos rather than the Form's TopMost property,
                // because this thread must not touch the window's own state — and z-order is one of
                // the few things Windows is happy to be told from anywhere.
                if (DateTime.UtcNow - lastRaise > TimeSpan.FromMilliseconds(240))
                {
                    lastRaise = DateTime.UtcNow;
                    try { SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate); }
                    catch { /* going away */ }
                }

                // A short grace, or the buttons still held from summoning it would close it instantly.
                if (up < TimeSpan.FromMilliseconds(700)) continue;

                try
                {
                    if (Input.PadShortcut.Held().Combo == 0) continue;

                    Log.Info("Easter egg dismissed with the controller.");
                    Stop();
                    return;
                }
                catch { /* pad went away; the mouse and keyboard still work */ }
            }
        });

        form.FormClosed += (_, _) =>
        {
            // Silence first: a song outliving the picture is worse than no song at all.
            try { song?.Stop(); } catch { /* best effort */ }
            song?.Dispose();
            tune?.Dispose();

            view.Image = null;
            image.Dispose();
            if (ReferenceEquals(_open, form)) _open = null;
        };

        _open = form;

        form.Show();
        form.Activate();
    }
}
