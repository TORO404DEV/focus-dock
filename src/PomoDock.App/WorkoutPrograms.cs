namespace PomoDock.App;

/// <summary>
/// Built-in home programs matching the user's mancuernas + flat-bench split.
/// </summary>
internal static class WorkoutPrograms
{
    internal sealed class Program
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Cadence { get; set; } = "";
        public string CoverId { get; set; } = "";
        public int RestSet { get; set; } = 60;
        public int RestMove { get; set; } = 60;
        public List<Day> Days { get; set; } = [];
    }

    internal sealed class Day
    {
        public string Label { get; set; } = "";
        public int Calories { get; set; }
        public int Minutes { get; set; }
        public List<Move> Moves { get; set; } = [];
    }

    internal sealed class Move
    {
        public string Name { get; set; } = "";
        public string CatalogId { get; set; } = "";
        public int[] Reps { get; set; } = [];
        public double Kg { get; set; }
    }

    internal static IReadOnlyList<Program> All { get; } =
    [
        Beginner(),
        Cut(),
        FullBody(),
        Split(),
        Tone()
    ];

    internal static Program? Find(string? id, IEnumerable<Program>? extra = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var hit = All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;
        return extra?.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    internal static int EstimateCal(Day day) => Math.Max(40, VolumeKg(day) / 16);
    internal static int EstimateMin(Day day, int restSet)
    {
        int sets = day.Moves.Sum(m => Math.Max(1, m.Reps.Length));
        return Math.Max(8, (int)Math.Round(sets * (restSet + 22) / 60.0));
    }

    internal static string Spanish(string catalogId, string fallback = "")
    {
        if (Names.TryGetValue(catalogId, out var name)) return name;
        var ex = WorkoutCatalog.Find(catalogId);
        return ex?.Name ?? fallback;
    }

    internal static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["0361"] = "Press de hombro",
        ["2137"] = "Press Arnold",
        ["0293"] = "Remo inclinado",
        ["0294"] = "Curl de bíceps",
        ["0296"] = "Press cerrado",
        ["0297"] = "Curl concentrado",
        ["0298"] = "Curl martillo",
        ["0300"] = "Peso muerto",
        ["0308"] = "Aperturas en banco",
        ["0310"] = "Elevaciones frontales",
        ["1760"] = "Sentadillas",
        ["0332"] = "Cruces",
        ["0333"] = "Patada de tríceps",
        ["0334"] = "Elevaciones laterales",
        ["0336"] = "Zancadas",
        ["0340"] = "Press de banca",
        ["0351"] = "Extensión de tríceps acostado",
        ["1328"] = "Pájaros acostado",
        ["0377"] = "Pájaros",
        ["0406"] = "Encogimiento de hombros",
        ["0413"] = "Sentadillas",
        ["0417"] = "Elevación de pantorrillas",
        ["0432"] = "Peso muerto rumano",
        ["2188"] = "Extensión de tríceps",
        ["0350"] = "Curl de bíceps acostado",
        ["1329"] = "Remo amplio",
        ["0407"] = "Inclínación lateral",
        ["0396"] = "Elevaciones laterales sentado",
        ["0394"] = "Patada de tríceps sentado"
    };

    internal static Program Clone(Program src)
    {
        return new Program
        {
            Id = src.Id,
            Name = src.Name,
            Cadence = src.Cadence,
            CoverId = src.CoverId,
            RestSet = src.RestSet,
            RestMove = src.RestMove,
            Days = src.Days.Select(d => new Day
            {
                Label = d.Label,
                Calories = d.Calories,
                Minutes = d.Minutes,
                Moves = d.Moves.Select(m => new Move
                {
                    Name = m.Name,
                    CatalogId = m.CatalogId,
                    Reps = [.. m.Reps],
                    Kg = m.Kg
                }).ToList()
            }).ToList()
        };
    }

    internal static int VolumeKg(Day day)
    {
        int sum = 0;
        foreach (var move in day.Moves)
            sum += (int)Math.Round(2 * move.Kg * move.Reps.Sum());
        return sum;
    }

    internal static string Scheme(Move move) =>
        move.Reps.Length <= 1 ? move.Reps[0].ToString() : string.Join(" + ", move.Reps);

    private static Program Beginner() => new()
    {
        Id = "principiante",
        Name = "Principiante",
        Cadence = "Diariamente",
        CoverId = "0294",
        RestSet = 30,
        RestMove = 60,
        Days =
        [
            new Day
            {
                Label = "",
                Calories = 83,
                Minutes = 15,
                Moves =
                [
                    Make("Extensión de tríceps", "2188", 6, 12),
                    Make("Press de hombro", "0361", 6, 12),
                    Make("Elevaciones laterales", "0334", 6, 12),
                    Make("Curl de bíceps alterno", "0294", 6, 12),
                    Make("Sentadillas", "1760", 6, 10),
                    Make("Elevación de pantorrillas", "0417", 6, 15)
                ]
            }
        ]
    };

    private static Program Cut() => new()
    {
        Id = "perdida",
        Name = "Pérdida de peso",
        Cadence = "Diariamente",
        CoverId = "1760",
        RestSet = 30,
        RestMove = 45,
        Days =
        [
            new Day
            {
                Label = "",
                Calories = 306,
                Minutes = 20,
                Moves =
                [
                    Make("Zancadas", "0336", 5, 12, 12),
                    Make("Sentadillas", "1760", 5, 10, 10),
                    Make("Pájaros", "0377", 5, 15, 15),
                    Make("Aperturas en banco", "0308", 5, 15, 15),
                    Make("Press de banca", "0340", 5, 15, 15),
                    Make("Elevaciones laterales", "0334", 5, 12, 12),
                    Make("Press de hombro", "0361", 5, 15, 15),
                    Make("Curl de bíceps", "0294", 5, 12, 12),
                    Make("Extensión de tríceps", "2188", 5, 12, 12),
                    Make("Elevación de pantorrillas", "0417", 5, 20, 20)
                ]
            }
        ]
    };

    private static Program FullBody() => new()
    {
        Id = "completo",
        Name = "Cuerpo completo",
        Cadence = "3 días a la semana",
        CoverId = "0413",
        RestSet = 45,
        RestMove = 60,
        Days =
        [
            new Day
            {
                Label = "Día 1",
                Calories = 180,
                Minutes = 25,
                Moves =
                [
                    Make("Sentadillas", "1760", 8, 12, 10, 10),
                    Make("Press de banca", "0340", 8, 12, 10, 10),
                    Make("Remo inclinado", "0293", 8, 12, 10, 10),
                    Make("Curl de bíceps", "0294", 8, 12, 10)
                ]
            },
            new Day
            {
                Label = "Día 2",
                Calories = 180,
                Minutes = 25,
                Moves =
                [
                    Make("Zancadas", "0336", 8, 12, 10, 10),
                    Make("Press de hombro", "0361", 8, 12, 10, 10),
                    Make("Pájaros", "0377", 8, 12, 10, 10),
                    Make("Extensión de tríceps", "2188", 8, 12, 10)
                ]
            },
            new Day
            {
                Label = "Día 3",
                Calories = 180,
                Minutes = 25,
                Moves =
                [
                    Make("Peso muerto", "0300", 8, 12, 10, 10),
                    Make("Elevaciones laterales", "0334", 8, 12, 10, 10),
                    Make("Aperturas en banco", "0308", 8, 12, 10, 10),
                    Make("Elevación de pantorrillas", "0417", 8, 15, 12, 12)
                ]
            }
        ]
    };

    private static Program Split() => new()
    {
        Id = "dividida",
        Name = "Rutina dividida total",
        Cadence = "5 días a la semana",
        CoverId = "0300",
        RestSet = 60,
        RestMove = 60,
        Days =
        [
            new Day
            {
                Label = "Día 1",
                Calories = 196,
                Minutes = 30,
                Moves =
                [
                    Make("Press de banca", "0340", 10, 12, 10, 10, 8),
                    Make("Aperturas en banco", "0308", 10, 12, 10, 10, 8),
                    Make("Press cerrado", "0296", 10, 12, 10, 10, 8),
                    Make("Cruces", "0332", 10, 12, 10, 10, 8)
                ]
            },
            new Day
            {
                Label = "Día 2",
                Calories = 207,
                Minutes = 30,
                Moves =
                [
                    Make("Pájaros", "0377", 10, 12, 10, 10, 8),
                    Make("Remo inclinado", "0293", 10, 12, 10, 10, 8),
                    Make("Remo amplio", "1329", 10, 12, 10, 10, 8),
                    Make("Encogimiento de hombros", "0406", 10, 15, 12, 12, 10)
                ]
            },
            new Day
            {
                Label = "Día 3",
                Calories = 233,
                Minutes = 40,
                Moves =
                [
                    Make("Curl de bíceps", "0294", 10, 12, 10, 8),
                    Make("Curl martillo", "0298", 10, 12, 10, 8),
                    Make("Curl de bíceps acostado", "0350", 10, 12, 10, 8),
                    Make("Patada de tríceps", "0333", 10, 12, 12, 10, 10),
                    Make("Extensión de tríceps acostado", "0351", 10, 12, 10, 8),
                    Make("Extensión de tríceps", "2188", 10, 12, 10, 8)
                ]
            },
            new Day
            {
                Label = "Día 4",
                Calories = 196,
                Minutes = 30,
                Moves =
                [
                    Make("Press de hombro", "0361", 10, 12, 10, 10, 8),
                    Make("Elevaciones laterales", "0334", 10, 12, 10, 10, 8),
                    Make("Press Arnold", "2137", 10, 12, 10, 10, 8),
                    Make("Elevaciones frontales", "0310", 10, 12, 10, 10, 8)
                ]
            },
            new Day
            {
                Label = "Día 5",
                Calories = 212,
                Minutes = 30,
                Moves =
                [
                    Make("Peso muerto", "0300", 10, 12, 10, 10, 8),
                    Make("Zancadas", "0336", 10, 12, 12, 10, 10),
                    Make("Sentadillas", "1760", 10, 12, 10, 10, 8),
                    Make("Elevación de pantorrillas", "0417", 10, 15, 12, 12, 10)
                ]
            }
        ]
    };

    private static Program Tone() => new()
    {
        Id = "definicion",
        Name = "Definición muscular",
        Cadence = "4 días a la semana",
        CoverId = "0334",
        RestSet = 45,
        RestMove = 60,
        Days =
        [
            new Day
            {
                Label = "Día 1",
                Calories = 160,
                Minutes = 25,
                Moves =
                [
                    Make("Press de banca", "0340", 8, 15, 12, 12),
                    Make("Aperturas en banco", "0308", 8, 15, 12, 12),
                    Make("Press de hombro", "0361", 8, 15, 12, 12),
                    Make("Elevaciones laterales", "0334", 8, 15, 12, 12)
                ]
            },
            new Day
            {
                Label = "Día 2",
                Calories = 160,
                Minutes = 25,
                Moves =
                [
                    Make("Remo inclinado", "0293", 8, 15, 12, 12),
                    Make("Pájaros", "0377", 8, 15, 12, 12),
                    Make("Encogimiento de hombros", "0406", 8, 15, 12, 12),
                    Make("Peso muerto", "0300", 8, 12, 10, 10)
                ]
            },
            new Day
            {
                Label = "Día 3",
                Calories = 160,
                Minutes = 25,
                Moves =
                [
                    Make("Curl de bíceps", "0294", 8, 15, 12, 12),
                    Make("Curl martillo", "0298", 8, 15, 12, 12),
                    Make("Extensión de tríceps", "2188", 8, 15, 12, 12),
                    Make("Patada de tríceps", "0333", 8, 15, 12, 12)
                ]
            },
            new Day
            {
                Label = "Día 4",
                Calories = 160,
                Minutes = 25,
                Moves =
                [
                    Make("Sentadillas", "1760", 8, 15, 12, 12),
                    Make("Zancadas", "0336", 8, 15, 12, 12),
                    Make("Peso muerto", "0432", 8, 12, 10, 10),
                    Make("Elevación de pantorrillas", "0417", 8, 20, 15, 15)
                ]
            }
        ]
    };

    private static Move Make(string name, string id, double kg, params int[] reps) =>
        new() { Name = name, CatalogId = id, Kg = kg, Reps = reps };
}
