using RunAsAdmin.Shared;

// Kinder-Tool / CLI (fuer Skripte/Verknuepfungen). Die grafische Variante ist
// RunAsAdmin.Gui. Aufrufformen:
//   runasadmin list                       -> zeigt freigegebene Programme
//   runasadmin run "UniGetUI"             -> fordert Start an
//   runasadmin run "UniGetUI" --reason ".."

try
{
    return await Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 2;
}

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        PrintHelp();
        return 0;
    }

    switch (args[0].ToLowerInvariant())
    {
        case "list":
            var list = await PipeClient.ListAppsAsync();
            foreach (var a in list.Apps)
                Console.WriteLine($"  {a.Name}{(a.AutoApprove ? " (sofort)" : " (Freigabe noetig)")}" +
                                  $"{(a.Available ? "" : "  [Datei fehlt]")}");
            return 0;

        case "run":
            if (args.Length < 2) { Console.Error.WriteLine("Name fehlt."); return 1; }
            var resp = await PipeClient.RunAsync(args[1], ReadOption(args, "--reason"));
            Console.WriteLine(resp.Message);
            return resp.State == RequestState.Approved ? 0 : 1;

        case "request":
            // Unbekannte .exe zur Freigabe anfragen (Explorer-Rechtsklick).
            if (args.Length < 2) { Console.Error.WriteLine("Pfad fehlt."); return 1; }
            var rr = await PipeClient.RequestPathAsync(args[1], ReadOption(args, "--reason"));
            Console.WriteLine(rr.Message);
            return rr.State == RequestState.Approved ? 0 : 1;

        default:
            Console.Error.WriteLine($"Unbekannter Befehl: {args[0]}");
            PrintHelp();
            return 1;
    }
}

static string? ReadOption(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void PrintHelp()
{
    Console.WriteLine("""
        RunAsAdmin – Programme mit Freigabe erhoeht starten

          runasadmin list                    Zeigt freigegebene Programme
          runasadmin run "<Name>"            Startet ein freigegebenes Programm
          runasadmin run "<Name>" --reason "Grund"

        Fuer Kinder gibt es die grafische Variante (RunAsAdmin.Gui).
        """);
}
