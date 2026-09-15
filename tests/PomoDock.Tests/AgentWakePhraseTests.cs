using PomoDock.Core;

internal static class AgentWakePhraseTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("hey pomodock strips the wake name and keeps the order", () =>
        {
            assert(AgentWakePhrase.TakeCommand("hey pomodock abre brave en youtube") == "abre brave en youtube", "command remains");
        });

        test("whisper splits pomo dock the same way", () =>
        {
            assert(AgentWakePhrase.TakeCommand("oye pomo dock reproduce amor eterno") == "reproduce amor eterno", "split name still wakes");
        });

        test("nearby slips still wake and pomodoro still does not", () =>
        {
            assert(AgentWakePhrase.TakeCommand("hey promo dock abre brave") == "abre brave", "promo dock");
            assert(AgentWakePhrase.TakeCommand("hola pomodoque cambia la cancion") == "cambia la cancion", "spanish slip");
            assert(AgentWakePhrase.TakeCommand("eh pomodok pon lofi") == "pon lofi", "pomodok");
            assert(AgentWakePhrase.TakeCommand("inicia el pomodoro de veinticinco") is null, "timer talk is ignored");
        });

        test("pomodoro is not a wake word", () =>
        {
            assert(AgentWakePhrase.TakeCommand("inicia el pomodoro de veinticinco") is null, "timer talk is ignored");
        });

        test("speech without the name is ignored", () =>
        {
            assert(AgentWakePhrase.TakeCommand("abre una ventana con brave") is null, "no wake");
        });

        test("only the wake name is an empty command", () =>
        {
            assert(AgentWakePhrase.TakeCommand("hey pomodock") == "", "listen ack");
            assert(AgentWakePhrase.TakeCommand("PomoDock,") == "", "comma still empty");
        });

        test("a custom wake name is respected", () =>
        {
            assert(AgentWakePhrase.TakeCommand("hey luna abre notas", "luna") == "abre notas", "custom name");
            assert(AgentWakePhrase.TakeCommand("hey pomodock abre notas", "luna") is null, "default name is not forced");
        });

        test("whisper slips of pomodock still wake and pomodoro still does not", () =>
        {
            assert(AgentWakePhrase.TakeCommand("el hipomodo reproduce amor eterno de natanel cano") == "reproduce amor eterno de natanel cano", "hipomodo");
            assert(AgentWakePhrase.TakeCommand("Hey, Pomalog!") == "", "pomalog arms");
            assert(AgentWakePhrase.TakeCommand("Ej pomodorka") == "", "pomodorka arms");
            assert(AgentWakePhrase.TakeCommand("Hey Pomodok, reproduce amor eterno de Natanayel Kano") == "reproduce amor eterno de natanayel kano", "pomodok with hey");
            assert(AgentWakePhrase.TakeCommand("abre una ventana con brave") is null, "no wake still ignored");
            assert(AgentWakePhrase.TakeCommand("inicia el pomodoro de veinticinco") is null, "timer word");
            assert(AgentWakePhrase.TakeCommand("abre la pomada") is null, "pomada is not the name");
            assert(AgentWakePhrase.TakeCommand("hey pimiento") is null, "pimiento is not the name");
            assert(AgentWakePhrase.TakeCommand("Pomog abre brave") == "abre brave", "pomog");
            assert(AgentWakePhrase.TakeCommand("Pomodo busca marcha") == "busca marcha", "pomodo");
        });

        test("a spoken order runs even without the wake name", () =>
        {
            string? subjunctive = AgentWakePhrase.TakeSpokenOrder("que reproduzcas marcia y el oso en Youtube");
            assert(subjunctive is not null && subjunctive.Contains("marcia", StringComparison.OrdinalIgnoreCase) && subjunctive.Contains("oso", StringComparison.OrdinalIgnoreCase), "subjunctive play: [" + (subjunctive ?? "null") + "]");
            assert(AgentWakePhrase.TakeSpokenOrder("busca lofi en youtube") == "busca lofi en youtube", "search");
            assert(AgentWakePhrase.TakeSpokenOrder("abre una ventana con brave") == "abre una ventana con brave", "open without wake");
            assert(AgentWakePhrase.TakeSpokenOrder("hey pomodock abre brave") == "abre brave", "wake still strips");
            assert(AgentWakePhrase.TakeSpokenOrder("A ver, le echaba Pomodo, busca marcha y lo so") == "busca marcha y lo so", "clips from the verb");
            assert(AgentWakePhrase.TakeSpokenOrder("I'll stay in the cell") is null, "lyrics");
            assert(AgentWakePhrase.TakeSpokenOrder("to join us") is null, "aside");
            assert(AgentWakePhrase.TakeSpokenOrder("Ich habe das gleiche Sitz auf den Skiff") is null, "foreign lyrics");
            assert(AgentWakePhrase.TakeSpokenOrder("No tiene ni tal, soy") is null, "small talk");
            assert(AgentWakePhrase.TakeSpokenOrder("inicia el pomodoro de veinticinco") is not null, "timer order still runs");
        });
    }
}

internal static class YouTubeWatchTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("the first videoRenderer id becomes a watch url", () =>
        {
            const string payload = """{"contents":{"videoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"Song"}]}}}}""";
            assert(YouTubeWatch.FirstVideoId(payload) == "dQw4w9WgXcQ", "first renderer");
            assert(YouTubeWatch.WatchUrl("dQw4w9WgXcQ") == "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "watch url");
        });

        test("a results url is a search and a watch url is not", () =>
        {
            string search = "https://www.youtube.com/results?search_query=amor+eterno";
            assert(YouTubeWatch.IsSearchResults(search), "search");
            assert(!YouTubeWatch.IsWatch(search), "search is not watch");
            assert(YouTubeWatch.QueryFromUrl(search) == "amor eterno", "query decoded");
            assert(YouTubeWatch.IsWatch("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), "watch");
        });

        test("playlist ids next to videoRenderer are skipped", () =>
        {
            const string payload = """{"playlistRenderer":{"videoId":"aaaaaaaaaaa"},"videoRenderer":{"videoId":"bbbbbbbbbbb"}}""";
            assert(YouTubeWatch.FirstVideoId(payload) == "bbbbbbbbbbb", "renderer wins");
        });

        test("a youtube caption is context and a bare browser is not", () =>
        {
            assert(YouTubeWatch.LooksLikeYouTubeContext("Natanael Cano - Amor Eterno - YouTube - Brave"), "watch title");
            assert(YouTubeWatch.LooksLikeYouTubeContext("Sin titulo", "https://www.youtube.com/watch?v=EO4prCs6WpM"), "watch url");
            assert(!YouTubeWatch.LooksLikeYouTubeContext("Sin titulo", null), "untitled widget");
            assert(!YouTubeWatch.LooksLikeYouTubeContext("Brave", null), "bare browser");
        });

        test("whisper tags and foley are junk", () =>
        {
            assert(WhisperTranscript.IsJunk("[BLANK_AUDIO]"), "blank audio");
            assert(WhisperTranscript.IsJunk("(typing)"), "typing");
            assert(WhisperTranscript.IsJunk("(keyboard clacking)"), "keyboard");
            assert(WhisperTranscript.IsJunk("[ukrainian idiom]"), "idiom tag");
            assert(WhisperTranscript.IsJunk("? ? ?"), "marks");
            assert(!WhisperTranscript.IsJunk("hey pomodock abre brave"), "real speech");
        });
    }
}

internal static class AgentListenVadTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("steady music never stays in capture and aborts before a long take", () =>
        {
            var vad = new AgentListenVad();
            var seen = new HashSet<AgentVadDecision>();
            for (int i = 0; i < 80; i++) seen.Add(vad.Push(0.08));
            assert(!vad.Capturing, "music is not an utterance");
            assert(!seen.Contains(AgentVadDecision.EndTimeout), "never waits out the max on music");
            assert(seen.Contains(AgentVadDecision.AbortMusic) || !seen.Contains(AgentVadDecision.BeginUtterance), "music is rejected");
        });

        test("speech-like bursts begin and end on real silence", () =>
        {
            var vad = new AgentListenVad();
            var seen = new HashSet<AgentVadDecision>();
            for (int i = 0; i < 12; i++) seen.Add(vad.Push(0.004));
            double[] burst = [0.055, 0.048, 0.012, 0.062, 0.018, 0.051, 0.009, 0.044];
            for (int round = 0; round < 6; round++)
                foreach (double rms in burst) seen.Add(vad.Push(rms));
            for (int i = 0; i < 20; i++) seen.Add(vad.Push(0.004));
            assert(seen.Contains(AgentVadDecision.BeginUtterance), "speech starts");
            assert(seen.Contains(AgentVadDecision.EndSilence), "speech ends on silence");
            assert(!seen.Contains(AgentVadDecision.AbortMusic), "modulated speech is not music");
            assert(!vad.Capturing, "capture released after silence");
        });
    }
}
